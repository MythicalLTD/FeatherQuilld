using System.Text.Json;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Proxy;

[Collection("Coraza")]
public class CaddyWafBuilderTests
{
    private const string Root = "/var/lib/fq";
    private static readonly Guid Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string Build(Action<WebSpace>? tweak = null, Action<WafSettings>? settings = null)
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = Root,
                Data = Root + "/data",
                DiskLimiterMode = "none",
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
            },
        };
        config.System.Quotas.Enabled = false;
        var space = new WebSpace
        {
            Uuid = Uuid,
            Runtime = "static",
            Domains = ["app.example.com", "www.example.com"],
            Ssl = true,
            WafEnabled = true,
            WafSettings = new WafSettings { Mode = "block" },
            BackendPort = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        settings?.Invoke(space.WafSettings!);
        tweak?.Invoke(space);
        return new ReverseProxyManager(config).BuildConfig([space]);
    }

    private static string Clean(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void NoSettings_KeepsLegacyOutput()
    {
        var text = Build(s => s.WafSettings = null);
        Assert.Contains("Strict-Transport-Security \"max-age=31536000;\"", text);
        Assert.Contains("max_size 10MB", text);
        Assert.DoesNotContain("fq_", text);
        Assert.DoesNotContain("handle_errors", text);
    }

    [Fact]
    public void Off_EmitsNothing()
    {
        var text = Build(settings: s => s.Mode = "off");
        Assert.DoesNotContain("fq_", text);
        Assert.DoesNotContain("request_body", text);
    }

    [Fact]
    public void Defaults_EmitHeadersLimitsAndBlockPage()
    {
        var text = Clean(Build());
        Assert.Contains("X-Content-Type-Options nosniff", text);
        Assert.Contains("X-Frame-Options SAMEORIGIN", text);
        Assert.Contains("Referrer-Policy strict-origin-when-cross-origin", text);
        Assert.DoesNotContain("Strict-Transport-Security", text);
        Assert.DoesNotContain("Permissions-Policy", text);
        Assert.Contains("max_size 10MB", text);
        Assert.Contains("expression `size({http.request.uri}) > 2048`", text);
        Assert.Contains($"error @fq_longurl \"fq-waf\" 414", text);
        Assert.Contains($"root * {Root}/proxy/waf/{Uuid:D}", text.Replace('\\', '/'));
        Assert.Contains("templates", text);
        // no method rule when every method is allowed
        Assert.DoesNotContain("fq_method", text);
    }

    [Fact]
    public void Headers_FollowSettings()
    {
        var text = Build(settings: s => { s.Headers.Hsts = true; s.Headers.Permissions = true; s.Headers.Frame = "deny"; s.Headers.Nosniff = false; s.Headers.Referrer = false; });
        Assert.Contains("Strict-Transport-Security", text);
        Assert.Contains("Permissions-Policy \"camera=(), microphone=(), geolocation=()\"", text);
        Assert.Contains("X-Frame-Options DENY", text);
        Assert.DoesNotContain("nosniff", text);
        Assert.DoesNotContain("Referrer-Policy", text);

        var noTls = Build(s => s.Ssl = false, s => s.Headers.Hsts = true);
        Assert.DoesNotContain("Strict-Transport-Security", noTls);

        var off = Build(settings: s => s.Headers.Frame = "off");
        Assert.DoesNotContain("X-Frame-Options", off);
    }

    [Fact]
    public void Methods_And_BodyLimit()
    {
        var text = Clean(Build(settings: s => { s.Http.Methods = ["GET", "POST"]; s.Http.MaxBodyMb = 64; s.Http.MaxUrlLength = 512; }));
        Assert.Contains("not method GET POST", text);
        Assert.Contains("error @fq_method \"fq-waf\" 405", text);
        Assert.Contains("max_size 64MB", text);
        Assert.Contains("int({http.request.header.Content-Length}) > 64000000", text);
        Assert.Contains("error @fq_bigbody \"fq-waf\" 413", text);
        Assert.Contains("> 512`", text);
    }

    [Fact]
    public void DenyLists_AreExpandedAndGuardedByAllowList()
    {
        var text = Clean(Build(
            sp => { sp.WafDenyIps = ["203.0.113.10", "198.51.100.0/24"]; sp.WafDenyPaths = ["/xmlrpc.php", "/wp-admin/"]; },
            s => s.AllowIps = ["192.0.2.7"]));
        Assert.Contains("remote_ip 203.0.113.10 198.51.100.0/24", text);
        Assert.Contains("not remote_ip 192.0.2.7", text);
        Assert.Contains("path /xmlrpc.php /xmlrpc.php/* /wp-admin /wp-admin/*", text);
        Assert.Contains("error @fq_denyip \"fq-waf\" 403", text);
        Assert.Contains("error @fq_denypath \"fq-waf\" 403", text);
    }

    [Fact]
    public void BlockStatus_IsUsedForListRules()
    {
        var text = Build(sp => sp.WafDenyIps = ["203.0.113.10"], s => s.BlockPage.Status = 429);
        Assert.Contains("error @fq_denyip \"fq-waf\" 429", text);
    }

    [Fact]
    public void Injection_ThroughListsIsDropped()
    {
        var text = Clean(Build(
            sp =>
            {
                sp.WafDenyPaths = ["/ok", "/a\nrespond 200", "/b {env.HOME}", "/c\"d", "/e`f", "/g\\h", "/x }"];
                sp.WafDenyIps = ["203.0.113.1\nrespond 200", "203.0.113.2"];
            },
            s =>
            {
                s.AntiBot.CustomAgents = ["Bad/1.0", "x\nrespond 200", "a{env.HOME}", "ev\"il", "Good-Bot"];
                s.Hotlink = new WafHotlink { Enabled = true, AllowedHosts = ["partner.example.com", "x.com\nrespond 200", "{env.HOME}.com"], Extensions = ["jpg", "j}g", "png"] };
            }));
        Assert.DoesNotContain("respond 200", text);
        Assert.DoesNotContain("{env", text);
        Assert.DoesNotContain("\"d", text.Replace("\"fq-waf\"", ""));
        Assert.Contains("path /ok /ok/*", text);
        Assert.Contains("remote_ip 203.0.113.2", text);
    }

    [Fact]
    public void Hotlink_UsesOwnDomainsAndAllowedHosts()
    {
        var text = Clean(Build(settings: s => s.Hotlink = new WafHotlink { Enabled = true, AllowedHosts = ["partner.example.com", "*.cdn.example.org"], Extensions = ["jpg", "png"] }));
        Assert.Contains("path_regexp fq_hot (?i)\\.(jpg|png)$", text);
        Assert.Contains("header Referer *", text);
        Assert.Contains("app\\.example\\.com|www\\.example\\.com|partner\\.example\\.com|([a-z0-9-]+\\.)+cdn\\.example\\.org", text);
        Assert.Contains("error @fq_hotlink", text);

        var off = Build(settings: s => s.Hotlink = new WafHotlink { Enabled = false });
        Assert.DoesNotContain("fq_hotlink", off);
    }

    [Fact]
    public void EmptyAgentBlock_ExemptsOnlyTheMtaStsPolicy()
    {
        var text = Clean(Build(settings: s => s.AntiBot = new WafAntiBot { EmptyAgent = true }));
        var i = text.IndexOf("@fq_noagent {", StringComparison.Ordinal);
        Assert.True(i >= 0);
        var block = text[i..text.IndexOf('}', i)];
        Assert.Contains("not header User-Agent *", block);
        Assert.Contains("not path /.well-known/mta-sts.txt", block);
        Assert.DoesNotContain("/.well-known/*", block);
    }

    [Fact]
    public void EmptyAgentBlock_AlsoRefusesABlankUserAgentHeader()
    {
        // "header User-Agent *" treats a present-but-blank header as set, so a blank one needs its own matcher.
        var text = Clean(Build(settings: s => s.AntiBot = new WafAntiBot { EmptyAgent = true }));
        var i = text.IndexOf("@fq_blankagent {", StringComparison.Ordinal);
        Assert.True(i >= 0);
        var block = text[i..text.IndexOf('}', i)];
        Assert.Contains("header_regexp User-Agent ^\\s*$", block);
        Assert.Contains("not path /.well-known/mta-sts.txt", block);
        Assert.Contains("error @fq_blankagent", text.Replace("respond", "error"));
    }

    [Fact]
    public void AntiBot_OnlyInBlockMode()
    {
        void Bots(WafSettings s) { s.AntiBot = new WafAntiBot { ScannerPaths = true, BadAgents = true, EmptyAgent = true, CustomAgents = ["EvilBot"] }; }
        var monitor = Build(settings: s => { s.Mode = "detect"; Bots(s); });
        Assert.DoesNotContain("fq_scanner", monitor);
        Assert.DoesNotContain("fq_badagent", monitor);
        Assert.DoesNotContain("fq_noagent", monitor);
        Assert.DoesNotContain("fq_customagent", monitor);

        var block = Clean(Build(settings: Bots));
        Assert.Contains("path /.env* /.git", block);
        // ACME challenges must never be denied by any bot rule; the only .well-known mention allowed is the
        // MTA-STS exemption on the empty-agent matcher ("not path ..."), never a path that is blocked.
        Assert.DoesNotContain(".well-known/acme-challenge", block);
        // Every mention of .well-known must be that exemption: one per emitted empty-agent matcher.
        Assert.Equal(
            System.Text.RegularExpressions.Regex.Matches(block, "@fq_(noagent|blankagent) \\{").Count,
            System.Text.RegularExpressions.Regex.Matches(block, @"\.well-known").Count);
        Assert.Equal(
            System.Text.RegularExpressions.Regex.Matches(block, @"\.well-known").Count,
            System.Text.RegularExpressions.Regex.Matches(block, @"not path /\.well-known/mta-sts\.txt").Count);
        Assert.Contains("not path /.well-known/mta-sts.txt", block);
        Assert.Contains("header_regexp User-Agent (?i)(sqlmap|nikto", block);
        Assert.Contains("not header User-Agent *", block);
        Assert.Contains("header_regexp User-Agent (?i)(EvilBot)", block);
    }

    [Fact]
    public void Monitor_StillAppliesCustomerLists()
    {
        var text = Build(sp => sp.WafDenyIps = ["203.0.113.10"], s => s.Mode = "detect");
        Assert.Contains("fq_denyip", text);
    }

    [Fact]
    public void UnsafeRootDirectory_FallsBackToPlainResponse()
    {
        var config = new AppConfig
        {
            System = new SystemConfig { RootDirectory = "/var/lib/fq dir", Data = "/d", DiskLimiterMode = "none", Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" } },
        };
        config.System.Quotas.Enabled = false;
        var space = new WebSpace { Uuid = Uuid, Runtime = "static", Domains = ["a.example.com"], WafEnabled = true, WafSettings = new WafSettings { Mode = "block" }, WafDenyIps = ["203.0.113.5"], CreatedAt = DateTimeOffset.UtcNow };
        var text = new ReverseProxyManager(config).BuildConfig([space]);
        Assert.Contains("respond @fq_denyip \"Access denied\" 403", text);
        Assert.DoesNotContain("handle_errors", text);
    }

    [Fact]
    public void PathPatterns_SkipWellKnownAndRoot()
    {
        Assert.Equal(["/a", "/a/*", "/b/*"], CaddyWafBuilder.PathPatterns(["/a", "/b/*", "/.well-known/x", "/", "/A"]));
    }

    // ---- block page ----

    [Fact]
    public void BlockPage_CustomerBracesAreLiteral_OnlyOurActionsRun()
    {
        var html = "<style>b{x:1}</style><h1>{{status}}</h1>{{ env \"HOME\" }} {env.HOME} {{include \"/etc/hosts\"}} {{{ x }}} <i>{{id}}</i><b>{{ip}}</b>";
        var o = WafBlockPageFiles.Render(html, "msg");
        var actions = System.Text.RegularExpressions.Regex.Matches(o, "\\{\\{[^}]*\\}\\}").Select(m => m.Value).Distinct().ToList();
        Assert.All(actions, a => Assert.True(
            a == "{{\"{\"}}" || a == "{{placeholder \"http.error.status_code\"}}" || a == "{{placeholder \"http.request.uuid\"}}" || a == "{{placeholder \"http.request.remote.host\"}}", a));
        Assert.Contains("{{placeholder \"http.error.status_code\"}}", o);
        Assert.Contains("{{placeholder \"http.request.uuid\"}}", o);
        Assert.Contains("{{placeholder \"http.request.remote.host\"}}", o);
        Assert.True(!o.Contains('\u0001'), o.Replace("\u0001", "<SOH>"));
    }

    [Fact]
    public void BlockPage_MessageIsEscapedAndCannotInjectMarkers()
    {
        var o = WafBlockPageFiles.Render(null, "<script>alert(1)</script> {{ env \"HOME\" }} {{id}}");
        Assert.DoesNotContain("<script>", o);
        Assert.Contains("&lt;script&gt;", o);
        Assert.DoesNotContain("{{ env", o);
        Assert.Contains("Request ID {{placeholder \"http.request.uuid\"}}", o); // only the template's own marker
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(o, "http\\.request\\.uuid").Count);
    }

    [Fact]
    public void BlockPage_DefaultAndSync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-waf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var space = new WebSpace { Uuid = Uuid, WafEnabled = true, WafSettings = new WafSettings { Mode = "block" } };
            WafBlockPageFiles.Sync(root, space);
            var file = Path.Combine(WafBlockPageFiles.Directory(root, Uuid), WafBlockPageFiles.FileName);
            Assert.True(File.Exists(file));
            Assert.Contains("Access denied", File.ReadAllText(file));

            space.WafSettings!.BlockPage.Html = "<p>custom</p>";
            WafBlockPageFiles.Sync(root, space);
            Assert.Contains("custom", File.ReadAllText(file));

            space.WafEnabled = false;
            WafBlockPageFiles.Sync(root, space);
            Assert.False(Directory.Exists(WafBlockPageFiles.Directory(root, Uuid)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    // ---- sanitizer ----

    [Fact]
    public void Sanitizer_ClampsAndDropsInvalidValues()
    {
        var input = JsonSerializer.Deserialize<WafSettings>("""
        {"mode":"turbo","allow_ips":["203.0.113.1","bad"],
         "rate_limit":{"enabled":true,"per_minute":1,"ban_minutes":99999},
         "http":{"methods":["GET","TRACE"],"max_body_mb":0,"max_url_length":99999},
         "headers":{"frame":"allowall"},
         "geo":{"mode":"x","countries":["DE","de","DEU"]},
         "block_page":{"status":200,"message":"m","html":"h"},
         "custom_rules":[{"id":"a","target":"path","op":"regex","value":"x","action":"block","enabled":true},{"target":"nope","op":"equals","value":"x","action":"block","enabled":true}],
         "exceptions":[{"path":"/","categories":[]},{"path":"/ok","categories":["sqli","bogus"]},{"path":"/ok","categories":[]}]}
        """)!;
        var s = WafSettingsSanitizer.Sanitize(input);
        Assert.Equal("detect", s.Mode);
        Assert.Equal(["203.0.113.1"], s.AllowIps);
        Assert.Equal(10, s.RateLimit.PerMinute);
        Assert.Equal(1440, s.RateLimit.BanMinutes);
        Assert.Equal(["GET"], s.Http.Methods);
        Assert.Equal(1, s.Http.MaxBodyMb);
        Assert.Equal(8192, s.Http.MaxUrlLength);
        Assert.Equal("sameorigin", s.Headers.Frame);
        Assert.Equal("off", s.Geo.Mode);
        Assert.Equal(["DE"], s.Geo.Countries);
        Assert.Equal(403, s.BlockPage.Status);
        Assert.Single(s.CustomRules);
        Assert.Single(s.Exceptions);
        Assert.Equal(["sqli"], s.Exceptions[0].Categories);
    }

    [Fact]
    public void Sanitizer_NullAndOversizedHtml()
    {
        Assert.Equal("detect", WafSettingsSanitizer.Sanitize(null).Mode);
        var big = new WafSettings { BlockPage = new WafBlockPage { Html = new string('x', 70000) } };
        Assert.Equal("", WafSettingsSanitizer.Sanitize(big).BlockPage.Html);
    }

    [Theory]
    [InlineData("/ok", true)]
    [InlineData("ok", false)]
    [InlineData("/a b", false)]
    [InlineData("/a\"b", false)]
    [InlineData("/a{b", false)]
    [InlineData("/a`b", false)]
    [InlineData("/a\\b", false)]
    [InlineData("/a\nb", false)]
    [InlineData("/a#b", true)]
    public void IsValidPath(string path, bool ok) => Assert.Equal(ok, WafSettingsSanitizer.IsValidPath(path));

    [Fact]
    public void PanelPayload_RoundTripsAndOldPanelsStayNull()
    {
        var withSettings = JsonSerializer.Deserialize<FeatherQuilld.Utils.Remote.PanelWebSpaceConfig>("""{"waf_enabled":true,"waf_settings":{"mode":"block","http":{"max_body_mb":5}}}""")!;
        Assert.Equal("block", withSettings.WafSettings!.Mode);
        Assert.Equal(5, withSettings.WafSettings.Http.MaxBodyMb);
        Assert.Equal(2048, withSettings.WafSettings.Http.MaxUrlLength);
        var old = JsonSerializer.Deserialize<FeatherQuilld.Utils.Remote.PanelWebSpaceConfig>("""{"waf_enabled":true}""")!;
        Assert.Null(old.WafSettings);
    }

    /// <summary>
    /// Writes a real Caddyfile + block page + site files for an end-to-end run against a real Caddy binary.
    /// Only runs when FQ_CADDY_ROOT is set (see the Caddy end-to-end script); a no-op in normal test runs.
    /// </summary>
    [Fact]
    public void WriteEndToEndFixture_WhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("FQ_CADDY_ROOT");
        if (string.IsNullOrEmpty(root))
            return;

        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = root,
                Data = root + "/data",
                DiskLimiterMode = "none",
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
            },
        };
        config.System.Quotas.Enabled = false;

        WebSpace Make(string id, string domain, Action<WebSpace>? t = null, Action<WafSettings>? ws = null)
        {
            var sp = new WebSpace
            {
                Uuid = Guid.Parse(id), Runtime = "static", Domains = [domain], Ssl = false, WafEnabled = true, DocumentRoot = "",
                WafSettings = new WafSettings { Mode = "block" }, CreatedAt = DateTimeOffset.UtcNow,
            };
            ws?.Invoke(sp.WafSettings!);
            t?.Invoke(sp);
            Directory.CreateDirectory(Path.Combine(root, "data", sp.Uuid.ToString()));
            return sp;
        }

        var page = "<style>body{margin:0}</style><h1>Blocked {{status}}</h1><p>{{message}}</p>{{ env \"HOME\" }} {env.HOME} {{include \"/etc/hostname\"}}<i>{{id}}</i><b>{{ip}}</b>";
        var a = Make("aaaaaaaa-0000-0000-0000-000000000001", "waf-a.test",
            sp => sp.WafDenyPaths = ["/private"],
            ws =>
            {
                ws.Http.Methods = ["GET", "HEAD", "POST"];
                ws.Http.MaxUrlLength = 300;
                ws.Headers = new WafHeaders { Hsts = true, Nosniff = true, Referrer = true, Permissions = true, Frame = "deny" };
                ws.Hotlink = new WafHotlink { Enabled = true, AllowedHosts = ["partner.example.com"], Extensions = ["jpg", "png"] };
                ws.AntiBot = new WafAntiBot { ScannerPaths = true, BadAgents = true, EmptyAgent = true, CustomAgents = ["EvilBot"] };
                ws.BlockPage = new WafBlockPage { Status = 403, Message = "Not for you <b>", Html = page };
            });
        var monitor = Make("aaaaaaaa-0000-0000-0000-000000000002", "waf-monitor.test",
            sp => sp.WafDenyPaths = ["/private"],
            ws => { ws.Mode = "detect"; ws.AntiBot = new WafAntiBot { ScannerPaths = true, BadAgents = true }; });
        var deny = Make("aaaaaaaa-0000-0000-0000-000000000003", "waf-deny.test", sp => sp.WafDenyIps = ["127.0.0.1"]);
        var allow = Make("aaaaaaaa-0000-0000-0000-000000000004", "waf-allow.test", sp => sp.WafDenyIps = ["127.0.0.1"], ws => ws.AllowIps = ["127.0.0.1"]);
        var proxy = Make("aaaaaaaa-0000-0000-0000-000000000005", "waf-proxy.test", sp => sp.BackendPort = 9001, ws => ws.Http.MaxBodyMb = 1);
        var down = Make("aaaaaaaa-0000-0000-0000-000000000006", "waf-down.test", sp => sp.BackendPort = 9999);
        var legacy = Make("aaaaaaaa-0000-0000-0000-000000000007", "waf-legacy.test", sp => { sp.WafSettings = null; sp.WafDenyPaths = ["/private"]; });

        foreach (var sp in new[] { a, monitor })
        {
            var dir = Path.Combine(root, "data", sp.Uuid.ToString());
            File.WriteAllText(Path.Combine(dir, "index.html"), "hello " + sp.Domains[0]);
            File.WriteAllText(Path.Combine(dir, "pic.jpg"), "jpg");
            File.WriteAllText(Path.Combine(dir, "doc.html"), "doc");
            File.WriteAllText(Path.Combine(dir, ".env"), "VALUE=1");
            Directory.CreateDirectory(Path.Combine(dir, "private"));
            File.WriteAllText(Path.Combine(dir, "private", "x.html"), "private");
        }

        File.WriteAllText(Path.Combine(root, "Caddyfile"), new ReverseProxyManager(config).BuildConfig([a, monitor, deny, allow, proxy, down, legacy]));
        foreach (var sp in new[] { a, monitor, deny, allow, proxy, down })
            WafBlockPageFiles.Sync(root, sp);
    }

    /// <summary>Same idea as above, for the attack engine; used by the Coraza end-to-end script (FQ_CORAZA_ROOT).</summary>
    [Fact]
    public void WriteCorazaFixture_WhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("FQ_CORAZA_ROOT");
        if (string.IsNullOrEmpty(root))
            return;

        CorazaProbe.Override = true;
        CorazaFiles.Validator = _ => true;
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root, Data = root + "/data", DiskLimiterMode = "none",
                    Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
                },
            };
            config.System.Quotas.Enabled = false;

            WebSpace Make(int n, string domain, Action<WafSettings>? ws = null, string runtime = "static")
            {
                var sp = new WebSpace
                {
                    Uuid = Guid.Parse($"bbbbbbbb-0000-0000-0000-{n:D12}"), Runtime = runtime, Domains = [domain], Ssl = false,
                    WafEnabled = true, WafSettings = new WafSettings { Mode = "block" }, CreatedAt = DateTimeOffset.UtcNow,
                };
                ws?.Invoke(sp.WafSettings!);
                var dir = Path.Combine(root, "data", sp.Uuid.ToString());
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "index.html"), "hello " + domain);
                return sp;
            }

            var page = "<h1>Blocked {{status}}</h1>{{message}}<i>{{id}}</i>";
            var all = new List<WebSpace>
            {
                Make(1, "c-block.test", w =>
                {
                    w.BlockPage = new WafBlockPage { Status = 403, Message = "no", Html = page };
                    w.Exceptions = [new WafException { Path = "/api", Categories = ["sqli"] }, new WafException { Path = "/open", Categories = [] }];
                    w.CustomRules =
                    [
                        new WafCustomRule { Id = "old", Target = "path", Op = "starts", Value = "/old-admin", Action = "block", Enabled = true },
                        new WafCustomRule { Id = "bot", Target = "user_agent", Op = "regex", Value = "evil[a-z]+bot", Action = "block", Enabled = true },
                        new WafCustomRule { Id = "ok", Target = "user_agent", Op = "contains", Value = "trusted-scanner", Action = "allow", Enabled = true },
                    ];
                }),
                Make(2, "c-monitor.test", w =>
                {
                    w.Mode = "detect";
                    w.CustomRules = [new WafCustomRule { Id = "old", Target = "path", Op = "starts", Value = "/old-admin", Action = "block", Enabled = true }];
                }),
                Make(3, "c-allowip.test", w => w.AllowIps = ["127.0.0.1"]),
                Make(4, "c-429.test", w => w.BlockPage = new WafBlockPage { Status = 429, Message = "slow down", Html = page }),
                Make(5, "c-noxss.test", w => w.Engine.Categories["xss"] = false),
                Make(6, "c-pl2.test", w => w.Engine.Sensitivity = 2),
                Make(9, "c-pl3.test", w => w.Engine.Sensitivity = 3),
                Make(10, "c-noleak.test", w => w.Engine.Categories["leakage"] = false),
                Make(11, "c-nophp.test", w => w.Engine.Categories["php"] = false),
                Make(12, "c-wp1.test", w => w.Engine.Sensitivity = 1, "php"),
                Make(13, "c-wp2.test", w => w.Engine.Sensitivity = 2, "php"),
                Make(14, "c-wp3.test", w => w.Engine.Sensitivity = 3, "php"),
                Make(7, "c-twin.test"),
                Make(8, "c-off.test", w => w.Mode = "off"),
            };

            Directory.CreateDirectory(root);
            CorazaFiles.Sync(root, all);
            File.WriteAllText(Path.Combine(root, "Caddyfile"), new ReverseProxyManager(config).BuildConfig(all));
            foreach (var sp in all)
                WafBlockPageFiles.Sync(root, sp);

            // A rule set that .NET accepts and Go's RE2 does not (back-reference): used to prove Coraza's own validation catches it.
            var bad = new WafSettings { Mode = "block" };
            bad.CustomRules = [new WafCustomRule { Id = "bad", Target = "path", Op = "regex", Value = "(a)\\1", Action = "block", Enabled = true }];
            File.WriteAllText(Path.Combine(root, "bad.conf"), CorazaRules.Build(WafSettingsSanitizer.Sanitize(bad), CorazaFiles.AuditLogPath(root)));
        }
        finally
        {
            CorazaProbe.Override = null;
            CorazaFiles.Validator = null;
        }
    }
}
