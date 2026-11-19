using System.Net;
using System.Net.Sockets;
using System.Text;
using FeatherQuilld.Utils.Dns;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Dns;

public class PowerDnsZoneListTests
{
    // Captured shape of GET /api/v1/servers/localhost/zones from PowerDNS Authoritative 5.0.x (gsqlite3).
    // The API returns a bare JSON array, never an object envelope.
    internal const string Pdns5ZoneList = """
        [
          {
            "account": "",
            "catalog": "",
            "dnssec": false,
            "edited_serial": 2026111901,
            "id": "example.com.",
            "kind": "Native",
            "last_check": 0,
            "masters": [],
            "name": "example.com.",
            "notified_serial": 0,
            "serial": 2026111901,
            "url": "/api/v1/servers/localhost/zones/example.com."
          },
          {
            "account": "customer-42",
            "catalog": "catalog.featherquilld.",
            "dnssec": true,
            "edited_serial": 4294967295,
            "id": "=C3=BCber.example.",
            "kind": "Master",
            "last_check": 0,
            "masters": [],
            "name": "Shop.Example.ORG.",
            "notified_serial": 4294967295,
            "serial": 4294967295,
            "url": "/api/v1/servers/localhost/zones/=C3=BCber.example."
          }
        ]
        """;

    [Fact]
    public void ParseZoneList_ReadsPowerDns5BareArray()
    {
        var zones = PowerDnsManager.ParseZoneList(Pdns5ZoneList, out var next);

        Assert.Null(next);
        Assert.Equal(2, zones.Count);
        Assert.Equal("example.com", zones[0].Id);
        Assert.Equal("example.com", zones[0].Name);
        Assert.Equal("Native", zones[0].Kind);
        Assert.Equal("shop.example.org", zones[1].Name);
        Assert.Equal("Master", zones[1].Kind);
    }

    [Fact]
    public void ParseZoneList_EmptyArray_ReturnsNoZones()
    {
        Assert.Empty(PowerDnsManager.ParseZoneList("[]", out var next));
        Assert.Null(next);
    }

    [Fact]
    public void ParseZoneList_StillAcceptsDataEnvelope()
    {
        const string json = """{"data":[{"id":"a.test.","name":"a.test."}],"next":"http://127.0.0.1:8081/api/v1/servers/localhost/zones?page=2"}""";
        var zones = PowerDnsManager.ParseZoneList(json, out var next);

        Assert.Single(zones);
        Assert.Equal("a.test", zones[0].Name);
        Assert.Equal("/api/v1/servers/localhost/zones?page=2", next);
    }

    [Fact]
    public void ParseZoneList_ToleratesNullsNumbersAndJunk()
    {
        const string json = """[{"id":null,"name":"b.test.","kind":null},{"id":7,"name":null},null,"x",{"url":"/nothing"}]""";
        var zones = PowerDnsManager.ParseZoneList(json, out _);

        Assert.Equal(2, zones.Count);
        Assert.Equal("b.test", zones[0].Id);
        Assert.Null(zones[0].Kind);
        Assert.Equal("7", zones[1].Name);
    }

    [Fact]
    public void ParseZoneList_InvalidJson_ThrowsReadableError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PowerDnsManager.ParseZoneList("<html>", out _));
        Assert.Contains("unreadable zone list", ex.Message);
    }

    [Fact]
    public void DescribeApiError_IncludesPowerDnsErrorField()
    {
        var msg = PowerDnsManager.DescribeApiError("list zones", 401, """{"error":"Unauthorized"}""");
        Assert.Equal("PowerDNS API could not list zones (HTTP 401): Unauthorized", msg);
    }

    [Fact]
    public async Task ListZones_AgainstPowerDns5Response_ReturnsZones()
    {
        await using var server = await FakePdns.StartAsync(Pdns5ZoneList);
        var config = new AppConfig();
        config.System.Dns.PowerDnsApiUrl = server.BaseUrl;
        config.System.Dns.PowerDnsApiKey = "secret";

        var zones = await Task.Run(() => new PowerDnsManager(config).ListZones());

        Assert.Equal(2, zones.Count);
        Assert.Contains("GET /api/v1/servers/localhost/zones?dnssec=false ", server.RequestLine);
        Assert.Contains("X-API-Key: secret", server.RawRequest, StringComparison.OrdinalIgnoreCase);
        var json = System.Text.Json.JsonSerializer.Serialize(zones);
        Assert.Contains("\"name\":\"example.com\"", json);
        Assert.Contains("\"name\":\"shop.example.org\"", json);
    }

    [Fact]
    public async Task ListZones_ApiError_SurfacesStatusAndMessage()
    {
        await using var server = await FakePdns.StartAsync("""{"error":"Unauthorized"}""", status: "401 Unauthorized");
        var config = new AppConfig();
        config.System.Dns.PowerDnsApiUrl = server.BaseUrl;
        config.System.Dns.PowerDnsApiKey = "wrong";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => new PowerDnsManager(config).ListZones()));
        Assert.Contains("HTTP 401", ex.Message);
        Assert.Contains("Unauthorized", ex.Message);
    }

    /// <summary>One-shot HTTP/1.1 responder on loopback, enough to stand in for the PowerDNS API.</summary>
    private sealed class FakePdns : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;
        public string BaseUrl { get; }
        public string RawRequest { get; private set; } = "";
        public string RequestLine => RawRequest.Split("\r\n")[0];

        private FakePdns(TcpListener listener, string body, string status)
        {
            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _loop = Task.Run(() => ServeOnceAsync(body, status));
        }

        public static Task<FakePdns> StartAsync(string body, string status = "200 OK")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakePdns(listener, body, status));
        }

        private async Task ServeOnceAsync(string body, string status)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                    break;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            RawRequest = request.ToString();
            var payload = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nServer: PowerDNS/5.0.2\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _loop; } catch { /* listener stopped */ }
        }
    }
}

public class PowerDnsInstallConfigTests
{
    // /proc/net/udp on Ubuntu 26.04 with systemd-resolved: 127.0.0.53:53 and 127.0.0.54:53 are held.
    private const string ProcNetUdpWithResolved = """
          sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode ref pointer drops
         1021: 3600007F:0035 00000000:0000 07 00000000:00000000 00:00000000 00000000   991        0 18334 2 0000000000000000 0
         1036: 3500007F:0035 00000000:0000 07 00000000:00000000 00:00000000 00000000   991        0 18332 2 0000000000000000 0
         5555: 00000000:0044 00000000:0000 07 00000000:00000000 00:00000000 00000000     0        0 19000 2 0000000000000000 0
        """;

    [Fact]
    public void FindLoopbackPort53Listeners_DetectsResolvedStub()
    {
        var stubs = PowerDnsInstallConfig.FindLoopbackPort53Listeners(ProcNetUdpWithResolved, null);
        Assert.Contains(IPAddress.Parse("127.0.0.53"), stubs);
        Assert.Contains(IPAddress.Parse("127.0.0.54"), stubs);
        Assert.Equal(2, stubs.Count);
    }

    [Fact]
    public void FindLoopbackPort53Listeners_DetectsIpv6Loopback()
    {
        const string udp6 = """
              sl  local_address                         remote_address                        st
             100: 00000000000000000000000001000000:0035 00000000000000000000000000000000:0000 07
             101: 00000000000000000000000000000000:0035 00000000000000000000000000000000:0000 07
            """;
        var stubs = PowerDnsInstallConfig.FindLoopbackPort53Listeners(null, udp6);
        Assert.Equal([IPAddress.IPv6Loopback], stubs);

        var addrs = PowerDnsInstallConfig.BuildLocalAddresses([IPAddress.Parse("203.0.113.10")], [IPAddress.Parse("127.0.0.53"), IPAddress.IPv6Loopback]);
        Assert.Equal(["203.0.113.10", "127.0.0.1"], addrs);
    }

    [Fact]
    public void FindLoopbackPort53Listeners_NoStub_ReturnsEmpty()
    {
        const string table = "  sl  local_address\n   0: 00000000:0044 00000000:0000 07\n";
        Assert.Empty(PowerDnsInstallConfig.FindLoopbackPort53Listeners(table));
    }

    [Fact]
    public void BuildLocalAddresses_PublicIpsPlusLoopback()
    {
        var addrs = PowerDnsInstallConfig.BuildLocalAddresses(
            [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("127.0.0.1"), IPAddress.Parse("fe80::1"), IPAddress.Parse("2001:db8::10")],
            [IPAddress.Parse("127.0.0.53")]);
        Assert.Equal(["203.0.113.10", "2001:db8::10", "127.0.0.1", "::1"], addrs);
    }

    [Fact]
    public void BuildConfig_WithoutConflict_UsesWildcard()
    {
        var conf = PowerDnsInstallConfig.BuildConfig("k", null);
        Assert.Contains("local-address=0.0.0.0,::\n", conf);
        Assert.Contains("launch=gsqlite3\n", conf);
        Assert.Contains("gsqlite3-database=/var/lib/powerdns/pdns.sqlite3\n", conf);
        Assert.DoesNotContain("nonexist-fail", conf);
    }

    [Fact]
    public void BuildConfig_WithExplicitAddresses_AvoidsResolvedStub()
    {
        var conf = PowerDnsInstallConfig.BuildConfig("k", ["203.0.113.10", "127.0.0.1", "::1"]);
        Assert.Contains("local-address=203.0.113.10,127.0.0.1,::1\n", conf);
        Assert.Contains("local-address-nonexist-fail=no\n", conf);
        Assert.DoesNotContain("0.0.0.0", conf);
    }

    [Fact]
    public void FindSqliteSchema_PrefersUbuntuSchemaDir()
    {
        var found = PowerDnsInstallConfig.FindSqliteSchema(p => p.StartsWith("/usr/share/", StringComparison.Ordinal));
        Assert.Equal("/usr/share/pdns-backend-sqlite3/schema/schema.sqlite3.sql", found);
        Assert.Null(PowerDnsInstallConfig.FindSqliteSchema(_ => false));
        Assert.Equal(
            "/usr/share/doc/pdns-backend-sqlite3/schema.sqlite3.sql",
            PowerDnsInstallConfig.FindSqliteSchema(p => p.Contains("/doc/pdns-backend-sqlite3/", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConflictingDropIns_IncludesUbuntuBindConf()
    {
        Assert.Contains("bind.conf", PowerDnsInstallConfig.ConflictingDropIns);
    }
}
