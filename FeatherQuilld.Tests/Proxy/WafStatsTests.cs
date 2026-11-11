using System.Text.Json;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Tests.Proxy;

[Collection("Coraza")]
public class WafStatsTests : IDisposable
{
    private static readonly Guid Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fq-stats-" + Guid.NewGuid().ToString("N"));

    public WafStatsTests() => CorazaProbe.Override = true;

    public void Dispose()
    {
        CorazaProbe.Override = null;
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private static string Access(DateTimeOffset t, string host, string uri, int status, string ip = "198.51.100.1", string ua = "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0 Safari/537.36",
        string? referer = null, bool errorHandled = false, string method = "GET")
    {
        var headers = $"\"User-Agent\":[\"{ua}\"]" + (referer is null ? "" : $",\"Referer\":[\"{referer}\"]");
        return "{\"level\":\"info\",\"ts\":" + (t.ToUnixTimeMilliseconds() / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
               ",\"logger\":\"http.log.access.log0\",\"msg\":\"handled request\",\"request\":{\"remote_ip\":\"" + ip + "\",\"client_ip\":\"" + ip + "\",\"method\":\"" + method +
               "\",\"host\":\"" + host + "\",\"uri\":\"" + uri + "\",\"headers\":{" + headers + "}},\"status\":" + status + (errorHandled ? ",\"uuid\":\"abc\"" : "") + "}";
    }

    private static string Audit(DateTimeOffset t, string host, string uri, bool interrupted, string tag = "attack-sqli", string ip = "203.0.113.9", string id = "tx1")
    {
        var msg = tag == "x" ? "" : $"Coraza: Warning. SQL Injection Attack Detected [id \\\"942100\\\"] [msg \\\"SQL Injection Attack Detected via libinjection\\\"] [tag \\\"{tag}\\\"] [tag \\\"OWASP_CRS\\\"]";
        var summary = "Coraza: Access denied (phase 2). Inbound Anomaly Score Exceeded [id \\\"949110\\\"] [msg \\\"Inbound Anomaly Score Exceeded (Total Score: 5)\\\"] [tag \\\"anomaly-evaluation\\\"]";
        return "{\"transaction\":{\"timestamp\":\"x\",\"unix_timestamp\":" + (t.ToUnixTimeMilliseconds() * 1_000_000) + ",\"id\":\"" + id + "\",\"client_ip\":\"" + ip +
               "\",\"server_id\":\"" + host + "\",\"request\":{\"method\":\"GET\",\"uri\":\"" + uri + "\"},\"is_interrupted\":" + (interrupted ? "true" : "false") +
               "},\"messages\":[{\"error_message\":\"" + msg + "\"},{\"error_message\":\"" + summary + "\"}]}";
    }

    private WebSpace Space() => new() { Uuid = Uuid, Domains = ["app.example.com", "www.example.com"], WafEnabled = true };

    private void WriteAccess(string domain, params string[] lines)
    {
        var path = ProxyAccessLogs.AccessLogPath(_root, Uuid, domain);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private void WriteAudit(params string[] lines)
    {
        var path = CorazaFiles.AuditLogPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static JsonElement Run(string root, WebSpace space, string range = "24h")
    {
        var result = WafStats.Compute(root, space, range, 50, Now);
        return JsonSerializer.SerializeToElement(result);
    }

    private void PutGeo(string code, params string[] lines)
    {
        var dir = WafGeoData.DataDirectory(_root);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, code + ".txt"), lines);
    }

    [Fact]
    public void CountryOf_FindsRanges_V4_V6_AndMappedAddresses()
    {
        PutGeo("DE", "5.1.0.0/16", "2a00:1450::/32");
        PutGeo("CN", "1.0.1.0/24", "1.0.2.0/23");
        Assert.Equal("DE", WafGeoData.CountryOf(_root, "5.1.200.3"));
        Assert.Equal("CN", WafGeoData.CountryOf(_root, "1.0.3.255"));
        Assert.Equal("DE", WafGeoData.CountryOf(_root, "2a00:1450:4001::1"));
        Assert.Equal("DE", WafGeoData.CountryOf(_root, "[2a00:1450::1]"));
        Assert.Equal("DE", WafGeoData.CountryOf(_root, "::ffff:5.1.0.1"));
        Assert.Null(WafGeoData.CountryOf(_root, "5.2.0.1"));      // just outside
        Assert.Null(WafGeoData.CountryOf(_root, "1.0.0.255"));    // just before
        Assert.Null(WafGeoData.CountryOf(_root, "1.0.4.0"));      // just after
        Assert.Null(WafGeoData.CountryOf(_root, "not an ip"));
        Assert.Null(WafGeoData.CountryOf(_root, ""));
        Assert.Null(WafGeoData.CountryOf(_root, null));
        Assert.Null(WafGeoData.CountryOf(_root, "2a01::1"));
    }

    [Fact]
    public void CountryOf_WithoutData_IsNull()
    {
        Assert.Null(WafGeoData.CountryOf(_root, "5.1.200.3"));
    }

    [Fact]
    public void Overview_CountsRequestsPerCountry()
    {
        PutGeo("DE", "5.1.0.0/16");
        PutGeo("US", "198.51.100.0/24");
        var t = Now.AddHours(-2);
        WriteAccess("app.example.com",
            Access(t, "app.example.com", "/", 200, ip: "5.1.0.7"),
            Access(t.AddMinutes(1), "app.example.com", "/", 200, ip: "5.1.0.8"),
            Access(t.AddMinutes(2), "app.example.com", "/", 200, ip: "198.51.100.5"),
            Access(t.AddMinutes(3), "app.example.com", "/", 200, ip: "192.0.2.1"));
        var countries = Run(_root, Space()).GetProperty("overview").GetProperty("countries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("count").GetInt32());
        Assert.Equal(2, countries["DE"]);
        Assert.Equal(1, countries["US"]);
        Assert.Equal(2, countries.Count); // the unknown address is not counted anywhere
    }

    [Fact]
    public void Totals_Series_AndTopLists()
    {
        var t = Now.AddHours(-2);
        WriteAccess("app.example.com",
            Access(t, "app.example.com", "/", 200, referer: "https://www.google.com/search"),
            Access(t.AddMinutes(1), "app.example.com", "/a?x=1", 200, ip: "198.51.100.2", ua: "curl/8.0"),
            Access(t.AddMinutes(2), "app.example.com", "/missing", 404, referer: "https://app.example.com/"),
            Access(t.AddMinutes(3), "app.example.com", "/boom", 502, ua: "Mozilla/5.0 (iPhone) Safari/604"),
            Access(Now.AddDays(-3), "app.example.com", "/old", 200));
        var json = Run(_root, Space());
        var o = json.GetProperty("overview");
        var totals = o.GetProperty("totals");
        Assert.Equal(4, totals.GetProperty("requests").GetInt32());
        Assert.Equal(2, totals.GetProperty("unique_ips").GetInt32());
        Assert.Equal(1, totals.GetProperty("status_4xx").GetInt32());
        Assert.Equal(1, totals.GetProperty("status_5xx").GetInt32());
        Assert.Equal(3600, o.GetProperty("bucket_seconds").GetInt32());
        Assert.Equal(25, o.GetProperty("series").GetArrayLength());
        Assert.Equal(4, o.GetProperty("series").EnumerateArray().Sum(p => p.GetProperty("requests").GetInt32()));
        Assert.Equal("www.google.com", o.GetProperty("referrers")[0].GetProperty("name").GetString());
        Assert.DoesNotContain(o.GetProperty("referrers").EnumerateArray(), r => r.GetProperty("name").GetString() == "app.example.com");
        var browsers = o.GetProperty("browsers").EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToList();
        Assert.Contains("Chrome", browsers);
        Assert.Contains("Bot / script", browsers);
        Assert.Contains("Safari", browsers);
        Assert.Contains(o.GetProperty("systems").EnumerateArray(), s => s.GetProperty("name").GetString() == "iOS");
        Assert.True(o.GetProperty("engine_available").GetBoolean());
    }

    [Fact]
    public void AuditEvents_AreFilteredByOwnDomains_AndCategorised()
    {
        var t = Now.AddMinutes(-30);
        WriteAudit(
            Audit(t, "app.example.com", "/p?id=1' OR 1=1", interrupted: true, id: "a1"),
            Audit(t.AddMinutes(1), "app.example.com", "/q", interrupted: false, tag: "attack-xss", id: "a2"),
            Audit(t.AddMinutes(2), "other.example.org", "/secret", interrupted: true, id: "a3"),
            Audit(t.AddMinutes(3), "www.example.com", "/c", interrupted: true, tag: "fq-custom", id: "a4"),
            Audit(Now.AddDays(-5), "app.example.com", "/old", interrupted: true, id: "a5"));
        var json = Run(_root, Space());
        var o = json.GetProperty("overview");
        Assert.Equal(2, o.GetProperty("totals").GetProperty("blocked").GetInt32());
        Assert.Equal(1, o.GetProperty("totals").GetProperty("detected").GetInt32());
        var cats = o.GetProperty("top_categories").EnumerateArray().ToDictionary(c => c.GetProperty("category").GetString()!, c => c.GetProperty("count").GetInt32());
        Assert.Equal(1, cats["sqli"]);
        Assert.Equal(1, cats["xss"]);
        Assert.Equal(1, cats["custom"]);
        var events = json.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        Assert.DoesNotContain(events, e => e.GetProperty("path").GetString() == "/secret");
        Assert.Equal("a4", events[0].GetProperty("id").GetString());
        var first = events.Single(e => e.GetProperty("id").GetString() == "a1");
        Assert.Equal("blocked", first.GetProperty("action").GetString());
        Assert.Equal("942100", first.GetProperty("rule_id").GetString());
        Assert.Equal("SQL Injection Attack Detected via libinjection", first.GetProperty("rule_msg").GetString());
        Assert.Equal("/p", first.GetProperty("path").GetString());
        Assert.Equal("203.0.113.9", o.GetProperty("top_ips")[0].GetProperty("ip").GetString());
    }

    [Fact]
    public void OwnRuleRefusals_AreCounted_AndCorazaBlocksNotTwice()
    {
        var t = Now.AddMinutes(-20);
        WriteAccess("app.example.com",
            Access(t, "app.example.com", "/.env", 403, ip: "198.51.100.77", errorHandled: true),
            Access(t.AddMinutes(1), "app.example.com", "/x", 405, errorHandled: true, method: "PUT"),
            Access(t.AddMinutes(2), "app.example.com", "/app-403", 403),   // the application's own 403: not ours
            Access(t.AddMinutes(3), "app.example.com", "/p", 403, ip: "203.0.113.9", errorHandled: true)); // also in the audit log
        WriteAudit(Audit(t.AddMinutes(3), "app.example.com", "/p?id=1", interrupted: true, id: "dup"));
        var json = Run(_root, Space());
        var totals = json.GetProperty("overview").GetProperty("totals");
        Assert.Equal(4, totals.GetProperty("requests").GetInt32());
        Assert.Equal(3, totals.GetProperty("blocked").GetInt32());
        var events = json.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.GetProperty("category").GetString() == "rules" && e.GetProperty("rule_msg").GetString() == "Request method not allowed");
        Assert.Equal(3, totals.GetProperty("blocked_ips").GetInt32());
    }

    [Fact]
    public void Ranges_ChangeBucketSize()
    {
        Assert.Equal(21600, Run(_root, Space(), "7d").GetProperty("overview").GetProperty("bucket_seconds").GetInt32());
        Assert.Equal(86400, Run(_root, Space(), "30d").GetProperty("overview").GetProperty("bucket_seconds").GetInt32());
        Assert.Equal(3600, Run(_root, Space(), "nonsense").GetProperty("overview").GetProperty("bucket_seconds").GetInt32());
    }

    [Fact]
    public void MissingFilesAndDamagedLines_GiveEmptyNumbers()
    {
        WriteAccess("app.example.com", "not json", "{\"broken\":", "{}", Access(Now.AddMinutes(-1), "app.example.com", "/", 200));
        WriteAudit("garbage", "{\"transaction\":{}}");
        var o = Run(_root, Space()).GetProperty("overview");
        Assert.Equal(1, o.GetProperty("totals").GetProperty("requests").GetInt32());
        Assert.Equal(0, o.GetProperty("totals").GetProperty("blocked").GetInt32());

        var empty = Run(Path.Combine(_root, "nothing"), new WebSpace { Uuid = Guid.NewGuid(), Domains = ["a.example.com"] });
        Assert.Equal(0, empty.GetProperty("overview").GetProperty("totals").GetProperty("requests").GetInt32());
        Assert.Equal(0, empty.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void WebSpaceWithoutDomains_NeverSeesAuditEntries()
    {
        WriteAudit(Audit(Now.AddMinutes(-5), "", "/x", interrupted: true), Audit(Now.AddMinutes(-5), "app.example.com", "/x", interrupted: true));
        var json = Run(_root, new WebSpace { Uuid = Uuid, Domains = [] });
        Assert.Equal(0, json.GetProperty("events").GetArrayLength());
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit Chrome/120 Safari/537", "Chrome", "Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) Edg/120.0", "Edge", "Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko Firefox/120.0", "Firefox", "Linux")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15) Version/17 Safari/605", "Safari", "macOS")]
    [InlineData("Googlebot/2.1 (+http://www.google.com/bot.html)", "Bot / script", "Other")]
    [InlineData("", "", "")]
    public void UserAgentGuess(string ua, string browser, string os)
    {
        Assert.Equal(browser, WafStats.Browser(ua));
        Assert.Equal(os, WafStats.OperatingSystem(ua));
    }

    private static int Requests(JsonElement json) => json.GetProperty("overview").GetProperty("totals").GetProperty("requests").GetInt32();

    [Fact]
    public void Absorb_CountsEachLineOnce_AndSurvivesEmptiedLogs()
    {
        var t = Now.AddMinutes(-40);
        WriteAccess("app.example.com", Access(t, "app.example.com", "/a", 200), Access(t.AddMinutes(1), "app.example.com", "/b", 200));
        Assert.Equal(2, Requests(Run(_root, Space())));
        Assert.Equal(2, Requests(Run(_root, Space())));

        // new lines are appended: only they are added
        File.AppendAllText(ProxyAccessLogs.AccessLogPath(_root, Uuid, "app.example.com"), Access(t.AddMinutes(2), "app.example.com", "/c", 200) + "\n");
        Assert.Equal(3, Requests(Run(_root, Space())));

        // the retention service empties the log: nothing is lost or counted again
        ProxyAccessLogs.RotateSpace(_root, Space());
        Assert.Empty(File.ReadAllText(ProxyAccessLogs.AccessLogPath(_root, Uuid, "app.example.com")));
        Assert.Equal(3, Requests(Run(_root, Space())));

        // lines written after the log was emptied start from the top of the file
        File.AppendAllText(ProxyAccessLogs.AccessLogPath(_root, Uuid, "app.example.com"), Access(t.AddMinutes(5), "app.example.com", "/d", 200) + "\n");
        Assert.Equal(4, Requests(Run(_root, Space())));
    }

    [Fact]
    public void Absorb_IgnoresAHalfWrittenLastLine_UntilItIsComplete()
    {
        var path = ProxyAccessLogs.AccessLogPath(_root, Uuid, "app.example.com");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Access(Now.AddMinutes(-5), "app.example.com", "/x", 200);
        File.WriteAllText(path, line[..40]);
        Assert.Equal(0, Requests(Run(_root, Space())));
        File.WriteAllText(path, line + "\n");
        Assert.Equal(1, Requests(Run(_root, Space())));
    }

    [Fact]
    public void Events_SurviveAuditLogRotation()
    {
        WriteAudit(Audit(Now.AddMinutes(-10), "app.example.com", "/p", interrupted: true, id: "keep"));
        Assert.Equal(1, Run(_root, Space()).GetProperty("events").GetArrayLength());

        CorazaFiles.RotateAuditLog(_root, maxBytes: 10);
        Assert.Equal(0, new FileInfo(CorazaFiles.AuditLogPath(_root)).Length);
        Assert.Equal(1, Run(_root, Space()).GetProperty("events").GetArrayLength());

        File.AppendAllText(CorazaFiles.AuditLogPath(_root), Audit(Now.AddMinutes(-5), "app.example.com", "/q", interrupted: false, tag: "attack-xss", id: "new") + "\n");
        var events = Run(_root, Space()).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["new", "keep"], events);
    }

    [Fact]
    public void RotateAuditLog_LeavesSmallFilesAlone()
    {
        WriteAudit(Audit(Now, "app.example.com", "/p", interrupted: true));
        var before = new FileInfo(CorazaFiles.AuditLogPath(_root)).Length;
        CorazaFiles.RotateAuditLog(_root);
        Assert.Equal(before, new FileInfo(CorazaFiles.AuditLogPath(_root)).Length);
        CorazaFiles.RotateAuditLog(Path.Combine(_root, "missing"));
    }

    [Fact]
    public void History_IsPrunedAndCapped_AndDamagedFileIsReplaced()
    {
        var path = WafStats.HistoryPath(_root, Uuid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        WriteAccess("app.example.com", Access(Now.AddMinutes(-3), "app.example.com", "/ok", 200));
        Assert.Equal(1, Requests(Run(_root, Space())));
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("not json", File.ReadAllText(path));

        WafStats.Delete(_root, Uuid);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void History_FileHoldsNoVisitorAddressesOnceTheHourIsOver()
    {
        var old = Now.AddHours(-5);
        WriteAccess("app.example.com", Access(old, "app.example.com", "/x", 200, ip: "198.51.100.50"));
        Run(_root, Space());
        var text = File.ReadAllText(WafStats.HistoryPath(_root, Uuid));
        Assert.DoesNotContain("198.51.100.50", text);
    }

    [Fact]
    public void RealLogs_WhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("FQ_STATS_ROOT");
        if (string.IsNullOrEmpty(root))
            return;
        foreach (var (n, domain) in new[] { (1, "c-block.test"), (2, "c-monitor.test") })
        {
            var space = new WebSpace { Uuid = Guid.Parse($"bbbbbbbb-0000-0000-0000-{n:D12}"), Domains = [domain] };
            File.WriteAllText(Path.Combine(root, $"stats-{domain}.json"), JsonSerializer.Serialize(WafStats.Compute(root, space, "24h", 50, DateTimeOffset.FromUnixTimeSeconds(1793419200)), new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
