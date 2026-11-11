using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Proxy;

[Collection("Coraza")]
public sealed class WafGeoRateLimitTests : IDisposable
{
    private static readonly Guid Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fq-geo-" + Guid.NewGuid().ToString("N"));

    public WafGeoRateLimitTests()
    {
        CorazaProbe.Override = false;
        CorazaProbe.RateLimitOverride = true;
        Directory.CreateDirectory(WafGeoData.DataDirectory(_root));
    }

    public void Dispose()
    {
        CorazaProbe.Override = null;
        CorazaProbe.RateLimitOverride = null;
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private void PutCountry(string code, params string[] lines) =>
        File.WriteAllLines(Path.Combine(WafGeoData.DataDirectory(_root), code + ".txt"), lines);

    private string Build(Action<WafSettings> settings, bool sync = true)
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = _root,
                Data = _root + "/data",
                DiskLimiterMode = "none",
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
            },
        };
        config.System.Quotas.Enabled = false;
        var space = new WebSpace
        {
            Uuid = Uuid,
            Runtime = "static",
            Domains = ["app.example.com"],
            Ssl = true,
            WafEnabled = true,
            WafSettings = new WafSettings { Mode = "block" },
            CreatedAt = DateTimeOffset.UtcNow,
        };
        settings(space.WafSettings!);
        if (sync)
            WafGeoData.Sync(_root, [space]);
        return new ReverseProxyManager(config).BuildConfig([space]).Replace("\r\n", "\n");
    }

    [Fact]
    public void ProxiedBackend_DoesNotLeakItsSoftwareVersion()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = _root, Data = _root + "/data", DiskLimiterMode = "none",
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
            },
        };
        config.System.Quotas.Enabled = false;
        var space = new WebSpace { Uuid = Uuid, Runtime = "php", Domains = ["app.example.com"], Ssl = true, BackendPort = 20123, CreatedAt = DateTimeOffset.UtcNow };
        var text = new ReverseProxyManager(config).BuildConfig([space]).Replace("\r\n", "\n");
        var i = text.IndexOf("reverse_proxy ", StringComparison.Ordinal);
        Assert.True(i >= 0);
        var block = text[i..text.IndexOf("\t}\n", i, StringComparison.Ordinal)];
        Assert.Contains("header_down -Server", block);
        Assert.Contains("header_down -X-Powered-By", block);
    }

    // ---- rate limit ----

    [Fact]
    public void RateLimit_EmitsZoneAndOrder()
    {
        var text = Build(s => { s.RateLimit.Enabled = true; s.RateLimit.PerMinute = 120; });
        Assert.Contains("order rate_limit before basic_auth", text);
        Assert.Contains("rate_limit {", text);
        Assert.Contains("zone fq11111111222233334444555555555555_all {", text);
        Assert.Contains("key {http.request.remote.host}", text);
        Assert.Contains("window 1m", text);
        Assert.Contains("events 120", text);
        Assert.DoesNotContain("zone fq11111111222233334444555555555555_login", text);
        Assert.Contains("{http.error.status_code} == 429", text);
    }

    [Fact]
    public void RateLimit_CountsPageRequestsOnly_NotAssets()
    {
        var text = Build(s => s.RateLimit.Enabled = true);
        var zone = text[text.IndexOf("zone fq11111111222233334444555555555555_all", StringComparison.Ordinal)..];
        zone = zone[..zone.IndexOf("events", StringComparison.Ordinal)];
        Assert.Contains("not path *.css *.js", zone);
        Assert.Contains("*.woff2", zone);
        Assert.Contains("*.png", zone);
        Assert.DoesNotContain("*.php", zone);
        Assert.DoesNotContain("*.html", zone);
    }

    [Fact]
    public void RateLimit_OffByDefault_AndNeedsTheModule_AndBlockMode()
    {
        Assert.DoesNotContain("rate_limit", Build(_ => { }));

        CorazaProbe.RateLimitOverride = false;
        Assert.DoesNotContain("rate_limit", Build(s => s.RateLimit.Enabled = true));

        CorazaProbe.RateLimitOverride = true;
        Assert.DoesNotContain("rate_limit", Build(s => { s.Mode = "detect"; s.RateLimit.Enabled = true; }));
    }

    [Fact]
    public void RateLimit_ExemptsAllowListedIps()
    {
        var text = Build(s => { s.RateLimit.Enabled = true; s.AllowIps = ["203.0.113.7"]; });
        var zone = text[text.IndexOf("zone fq11111111222233334444555555555555_all", StringComparison.Ordinal)..];
        zone = zone[..zone.IndexOf("events", StringComparison.Ordinal)];
        Assert.Contains("match {", zone);
        Assert.Contains("not remote_ip 203.0.113.7", zone);
    }

    [Fact]
    public void LoginLimit_UsesItsPaths()
    {
        var text = Build(s =>
        {
            s.Login.Enabled = true;
            s.Login.PerMinute = 5;
            s.Login.Paths = ["/wp-login.php", "/admin/*"];
        });
        Assert.Contains("zone fq11111111222233334444555555555555_login {", text);
        Assert.Contains("path /wp-login.php /wp-login.php/* /admin/*", text);
        Assert.Contains("events 5", text);
        Assert.DoesNotContain("zone fq11111111222233334444555555555555_all", text);
    }

    // ---- country filter ----

    [Fact]
    public void Geo_Deny_ImportsFileWithRanges()
    {
        PutCountry("CN", "# comment", "1.0.1.0/24", "1.0.2.0/23", "2001:db8::/32");
        PutCountry("RU", "5.8.0.0/19");
        var text = Build(s => { s.Geo.Mode = "deny"; s.Geo.Countries = ["CN", "RU"]; });
        Assert.Contains("@fq_geo {", text);
        var file = Directory.GetFiles(WafGeoData.SetsDirectory(_root), "*.caddy").Single();
        Assert.Contains("import " + file.Replace('\\', '/'), text.Replace('\\', '/'));
        var content = File.ReadAllText(file);
        Assert.StartsWith("remote_ip ", content);
        Assert.Contains("1.0.1.0/24", content);
        Assert.Contains("1.0.2.0/23", content);
        Assert.Contains("5.8.0.0/19", content);
        Assert.Contains("2001:db8::/32", content);
        Assert.Single(content.TrimEnd('\n').Split('\n'));
        Assert.Contains("error @fq_geo", text);
    }

    [Fact]
    public void Geo_Allow_KeepsPrivateRangesReachable()
    {
        PutCountry("DE", "5.1.0.0/16");
        Build(s => { s.Geo.Mode = "allow"; s.Geo.Countries = ["DE"]; });
        var content = File.ReadAllText(Directory.GetFiles(WafGeoData.SetsDirectory(_root), "*.caddy").Single());
        Assert.StartsWith("not remote_ip private_ranges 5.1.0.0/16", content);
    }

    [Fact]
    public void Geo_MissingData_FailsOpen()
    {
        // deny: unknown country is simply not blocked, and with no data at all no rule exists
        var none = Build(s => { s.Geo.Mode = "deny"; s.Geo.Countries = ["CN"]; });
        Assert.DoesNotContain("fq_geo", none);

        // allow with a country that has no data would lock that country out: not applied
        PutCountry("DE", "5.1.0.0/16");
        var allow = Build(s => { s.Geo.Mode = "allow"; s.Geo.Countries = ["DE", "AT"]; });
        Assert.DoesNotContain("fq_geo", allow);
    }

    [Fact]
    public void Geo_OnlyActsInBlockMode_AndIgnoresGarbage()
    {
        PutCountry("CN", "1.0.1.0/24", "evil }\nimport /etc/hosts", "1.2.3.4/33", "999.1.1.1/8", "10.0.0.0/8 extra", "not-an-ip");
        var monitor = Build(s => { s.Mode = "detect"; s.Geo.Mode = "deny"; s.Geo.Countries = ["CN"]; });
        Assert.DoesNotContain("fq_geo", monitor);

        Build(s => { s.Geo.Mode = "deny"; s.Geo.Countries = ["CN", "../etc", "X", "CNN"]; });
        var content = File.ReadAllText(Directory.GetFiles(WafGeoData.SetsDirectory(_root), "*.caddy").Single());
        Assert.Equal("remote_ip 1.0.1.0/24\n", content);
    }

    [Fact]
    public void Geo_Sync_RemovesUnusedFiles_AndDetectsDataChanges()
    {
        PutCountry("CN", "1.0.1.0/24");
        Build(s => { s.Geo.Mode = "deny"; s.Geo.Countries = ["CN"]; });
        Assert.Single(Directory.GetFiles(WafGeoData.SetsDirectory(_root)));
        Build(s => s.Geo.Mode = "off");
        Assert.Empty(Directory.GetFiles(WafGeoData.SetsDirectory(_root)));

        File.WriteAllText(Path.Combine(WafGeoData.DataDirectory(_root), "VERSION"), "2026-10-31\n");
        Assert.False(WafGeoData.DataChanged(_root));
        File.WriteAllText(Path.Combine(WafGeoData.DataDirectory(_root), "VERSION"), "2026-11-01\n");
        Assert.True(WafGeoData.DataChanged(_root));
        Assert.False(WafGeoData.DataChanged(_root));
    }

    /// <summary>Writes a real Caddyfile for the rate-limit / country-filter end-to-end script (FQ_RATE_ROOT).</summary>
    [Fact]
    public void WriteRateGeoFixture_WhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("FQ_RATE_ROOT");
        if (string.IsNullOrEmpty(root))
            return;

        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = root, Data = root + "/data", DiskLimiterMode = "none",
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
            },
        };
        config.System.Quotas.Enabled = false;
        Directory.CreateDirectory(WafGeoData.DataDirectory(root));
        File.WriteAllLines(Path.Combine(WafGeoData.DataDirectory(root), "CN.txt"), ["1.0.1.0/24", "127.0.0.0/8", "::1/128"]);
        File.WriteAllLines(Path.Combine(WafGeoData.DataDirectory(root), "DE.txt"), ["5.1.0.0/16"]);

        WebSpace Make(int n, string domain, Action<WafSettings> ws)
        {
            var sp = new WebSpace
            {
                Uuid = Guid.Parse($"cccccccc-0000-0000-0000-{n:D12}"), Runtime = "static", Domains = [domain], Ssl = false,
                WafEnabled = true, WafSettings = new WafSettings { Mode = "block" }, CreatedAt = DateTimeOffset.UtcNow,
            };
            ws(sp.WafSettings!);
            var dir = Path.Combine(root, "data", sp.Uuid.ToString());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"), "hello " + domain);
            File.WriteAllText(Path.Combine(dir, "login"), "login page");
            return sp;
        }

        var page = "<h1>Blocked {{status}}</h1>{{message}}";
        var all = new List<WebSpace>
        {
            Make(1, "r-rate.test", w =>
            {
                w.RateLimit.Enabled = true; w.RateLimit.PerMinute = 10;
                w.BlockPage = new WafBlockPage { Status = 403, Message = "slow down", Html = page };
            }),
            Make(11, "r-assets.test", w => { w.RateLimit.Enabled = true; w.RateLimit.PerMinute = 10; }),
            Make(2, "r-allow.test", w => { w.RateLimit.Enabled = true; w.RateLimit.PerMinute = 10; w.AllowIps = ["127.0.0.1"]; }),
            Make(3, "r-monitor.test", w => { w.Mode = "detect"; w.RateLimit.Enabled = true; w.RateLimit.PerMinute = 10; }),
            Make(4, "g-deny.test", w => { w.Geo.Mode = "deny"; w.Geo.Countries = ["CN"]; w.BlockPage = new WafBlockPage { Status = 403, Message = "geo", Html = page }; }),
            Make(5, "g-denyde.test", w => { w.Geo.Mode = "deny"; w.Geo.Countries = ["DE"]; }),
            Make(6, "g-allowde.test", w => { w.Geo.Mode = "allow"; w.Geo.Countries = ["DE"]; }),
            Make(7, "g-allowcn.test", w => { w.Geo.Mode = "allow"; w.Geo.Countries = ["CN"]; }),
            Make(8, "g-denyallowip.test", w => { w.Geo.Mode = "deny"; w.Geo.Countries = ["CN"]; w.AllowIps = ["127.0.0.1"]; }),
            Make(10, "r-login.test", w => { w.Login.Enabled = true; w.Login.PerMinute = 3; w.Login.Paths = ["/login"]; }),
            Make(9, "g-monitor.test", w => { w.Mode = "detect"; w.Geo.Mode = "deny"; w.Geo.Countries = ["CN"]; }),
        };
        WafGeoData.Sync(root, all);
        File.WriteAllText(Path.Combine(root, "Caddyfile"), new ReverseProxyManager(config).BuildConfig(all));
        foreach (var sp in all)
            WafBlockPageFiles.Sync(root, sp);
    }
}
