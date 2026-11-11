using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Proxy;

[Collection("Coraza")]
public class CorazaRulesTests : IDisposable
{
    private const string Audit = "/var/lib/fq/proxy/waf/audit.json";
    private static readonly Guid Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public CorazaRulesTests()
    {
        CorazaProbe.Override = true;
        CorazaFiles.Validator = _ => true;
    }

    public void Dispose()
    {
        CorazaProbe.Override = null;
        CorazaFiles.Validator = null;
    }

    private static WafSettings Settings(Action<WafSettings>? tweak = null)
    {
        var s = new WafSettings { Mode = "block" };
        tweak?.Invoke(s);
        return WafSettingsSanitizer.Sanitize(s);
    }

    private static string Build(Action<WafSettings>? tweak = null) => CorazaRules.Build(Settings(tweak), Audit);

    [Fact]
    public void Block_UsesCrsAtSensitivityAndBlocksAtDefaultThreshold()
    {
        var text = Build(s => s.Engine.Sensitivity = 2);
        Assert.Contains("Include @owasp_crs/*.conf", text);
        Assert.Contains("setvar:tx.blocking_paranoia_level=2,setvar:tx.detection_paranoia_level=2", text);
        Assert.DoesNotContain("anomaly_score_threshold", text);
        Assert.Contains($"SecAuditLog {Audit}", text);
        Assert.Contains("SecAuditLogParts AHZ", text);
        Assert.Contains("SecResponseBodyAccess Off", text);
        Assert.DoesNotContain("SecRuleRemoveByTag \"attack-sqli\"", text);
    }

    [Fact]
    public void Block_PassesTheCustomersMethodListToTheCrs()
    {
        // The CRS refuses everything except GET/HEAD/POST/OPTIONS (rule 911100) unless tx.allowed_methods says otherwise.
        var all = Build();
        Assert.Contains("setvar:'tx.allowed_methods=GET HEAD POST PUT PATCH DELETE OPTIONS'", all);
        var readOnly = Build(s => s.Http = new WafHttp { Methods = ["GET", "HEAD"] });
        Assert.Contains("setvar:'tx.allowed_methods=GET HEAD'", readOnly);
        Assert.DoesNotContain("PUT", readOnly.Split('\n').First(l => l.Contains("allowed_methods")));
    }

    [Fact]
    public void Monitor_OnlyLogsCrsHits_ButCustomRulesStillBlock()
    {
        var text = Build(s =>
        {
            s.Mode = "detect";
            s.CustomRules = [new WafCustomRule { Id = "x", Target = "path", Op = "starts", Value = "/old", Action = "block", Enabled = true }];
        });
        Assert.True(text.IndexOf("id:100100", StringComparison.Ordinal) < text.IndexOf("id:110900", StringComparison.Ordinal));
        Assert.Contains("SecRuleEngine On", text);
        Assert.DoesNotContain("anomaly_score_threshold", text);
        // CRS runs detection-only; customer deny rules (before this line) keep the engine On
        Assert.Contains("id:110900,phase:1,pass,nolog,ctl:ruleEngine=DetectionOnly", text);
        Assert.True(text.IndexOf("id:110900", StringComparison.Ordinal) < text.IndexOf("Include @owasp_crs", StringComparison.Ordinal));
        Assert.DoesNotContain("id:110900", Build());
    }

    [Fact]
    public void DisabledCategories_AreRemovedByTag()
    {
        var text = Build(s => { s.Engine.Categories["xss"] = false; s.Engine.Categories["rce"] = false; });
        Assert.Contains("SecRuleRemoveByTag \"attack-xss\"", text);
        Assert.Contains("SecRuleRemoveByTag \"attack-rce\"", text);
        Assert.Contains("SecRuleRemoveByTag \"attack-ssrf\"", text);
        Assert.DoesNotContain("SecRuleRemoveByTag \"attack-sqli\"", text);
        // leakage is off by default
        Assert.Contains("SecRuleRemoveByTag \"attack-disclosure\"", text);
    }

    [Fact]
    public void Leakage_OpensResponseBody()
    {
        var text = Build(s => s.Engine.Categories["leakage"] = true);
        Assert.Contains("SecResponseBodyAccess On", text);
        Assert.DoesNotContain("attack-disclosure", text);
    }

    [Fact]
    public void AllCategoriesOff_NoCrsAtAll()
    {
        var text = Build(s => { foreach (var c in CorazaRules.CategoryTags.Keys) s.Engine.Categories[c] = false; });
        Assert.DoesNotContain("@owasp_crs", text);
        Assert.DoesNotContain("crs-setup", text);
        Assert.DoesNotContain("SecRuleRemoveByTag", text);
    }

    [Fact]
    public void Exceptions_RemoveOnlyTheNamedCategories_AndComeAfterCustomRules()
    {
        var text = Build(s =>
        {
            s.Exceptions = [new WafException { Path = "/api/", Categories = ["sqli"] }, new WafException { Path = "/open", Categories = [] }];
            s.CustomRules = [new WafCustomRule { Id = "x", Target = "path", Op = "starts", Value = "/old", Action = "block", Enabled = true }];
        });
        Assert.Contains("@rx ^/api(/|$)\" \"id:110200,phase:1,pass,nolog,t:urlDecodeUni,t:normalizePath,ctl:ruleRemoveByTag=attack-sqli\"", text);
        Assert.Contains("@rx ^/open(/|$)\" \"id:110201,phase:1,pass,nolog,t:urlDecodeUni,t:normalizePath,ctl:ruleEngine=Off\"", text);
        Assert.True(text.IndexOf("id:100100", StringComparison.Ordinal) < text.IndexOf("id:110200", StringComparison.Ordinal));
        Assert.True(text.IndexOf("id:110201", StringComparison.Ordinal) < text.IndexOf("Include @owasp_crs", StringComparison.Ordinal));
    }

    [Fact]
    public void Exception_PathIsRegexEscaped()
    {
        var text = Build(s => s.Exceptions = [new WafException { Path = "/a.b+c(d)", Categories = ["xss"] }]);
        Assert.Contains("@rx ^/a\\.b\\+c\\(d\\)(/|$)", text);
    }

    [Fact]
    public void AllowIps_SwitchTheEngineOff()
    {
        var text = Build(s => s.AllowIps = ["192.0.2.7", "198.51.100.0/24"]);
        Assert.Contains("REMOTE_ADDR \"@ipMatch 192.0.2.7,198.51.100.0/24\" \"id:110100,phase:1,pass,nolog,ctl:ruleEngine=Off\"", text);
    }

    [Fact]
    public void BlockStatus_OverridesTheCrsBlockingRule()
    {
        Assert.Contains("SecRuleUpdateActionById 949110 \"deny,status:429\"", Build(s => s.BlockPage.Status = 429));
        Assert.DoesNotContain("SecRuleUpdateActionById", Build());
        Assert.DoesNotContain("SecRuleUpdateActionById", Build(s => { s.Mode = "detect"; s.BlockPage.Status = 429; }));
    }

    [Theory]
    [InlineData("path", "starts", "/old-admin", "SecRule REQUEST_FILENAME \"@beginsWith /old-admin\" \"id:100100,phase:1,t:urlDecodeUni,t:normalizePath,deny,status:403,log,auditlog,msg:'Custom rule r1',tag:'fq-custom'\"")]
    [InlineData("query", "contains", "UNION", "SecRule QUERY_STRING \"@contains UNION\" \"id:100100,phase:1,t:urlDecodeUni,deny")]
    [InlineData("user_agent", "contains", "EvilBot", "SecRule REQUEST_HEADERS:User-Agent \"@contains evilbot\" \"id:100100,phase:1,t:lowercase,deny")]
    [InlineData("user_agent", "regex", "Evil[Bb]ot\\S+", "\"@rx (?i)Evil[Bb]ot\\S+\" \"id:100100,phase:1,t:none,deny")]
    [InlineData("method", "equals", "put", "SecRule REQUEST_METHOD \"@streq PUT\" \"id:100100,phase:1,t:none,deny")]
    [InlineData("host", "equals", "Old.Example.com", "SecRule REQUEST_HEADERS:Host \"@streq old.example.com\"")]
    [InlineData("referer", "contains", "spam.example", "REQUEST_HEADERS:Referer \"@contains spam.example\"")]
    public void CustomRules_AreTranslated(string target, string op, string value, string expected)
    {
        var text = Build(s => s.CustomRules = [new WafCustomRule { Id = "r1", Target = target, Op = op, Value = value, Action = "block", Enabled = true }]);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void CustomRuleActions()
    {
        var text = Build(s => s.CustomRules =
        [
            new WafCustomRule { Id = "a", Target = "path", Op = "equals", Value = "/x", Action = "allow", Enabled = true },
            new WafCustomRule { Id = "l", Target = "path", Op = "equals", Value = "/y", Action = "log", Enabled = true },
            new WafCustomRule { Id = "d", Target = "path", Op = "equals", Value = "/z", Action = "block", Enabled = false },
        ]);
        Assert.Contains("id:100100,phase:1,t:urlDecodeUni,t:normalizePath,allow,nolog", text);
        Assert.Contains("id:100101,phase:1,t:urlDecodeUni,t:normalizePath,pass,log,auditlog,msg:'Custom rule l'", text);
        Assert.DoesNotContain("\"@streq /z\"", text);
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("ends-with-backslash\\")]
    [InlineData("%{TX.score}")]
    [InlineData("x%{ENV.HOME}")]
    [InlineData("tab\there")]
    public void CustomRules_WithDangerousValues_AreDropped(string value)
    {
        var text = Build(s => s.CustomRules = [new WafCustomRule { Id = "r1", Target = "path", Op = "contains", Value = value, Action = "block", Enabled = true }]);
        Assert.DoesNotContain("fq-custom", text);
    }

    [Fact]
    public void CustomRules_WithBrokenRegex_AreDropped()
    {
        var text = Build(s => s.CustomRules = [new WafCustomRule { Id = "r1", Target = "path", Op = "regex", Value = "([unclosed", Action = "block", Enabled = true }]);
        Assert.DoesNotContain("fq-custom", text);
    }

    [Fact]
    public void Hash_ChangesWithSettings_AndIsStable()
    {
        var a = CorazaRules.Hash(Build());
        Assert.Equal(a, CorazaRules.Hash(Build()));
        Assert.NotEqual(a, CorazaRules.Hash(Build(s => s.Engine.Sensitivity = 3)));
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void IsActive_FollowsModeAndEngine()
    {
        WebSpace Space(Action<WafSettings>? t = null) => new() { Uuid = Uuid, WafEnabled = true, WafSettings = Settings(t) };
        Assert.True(CorazaRules.IsActive(Space()));
        Assert.False(CorazaRules.IsActive(Space(s => s.Mode = "off")));
        Assert.False(CorazaRules.IsActive(new WebSpace { Uuid = Uuid, WafEnabled = false, WafSettings = Settings() }));
        Assert.False(CorazaRules.IsActive(new WebSpace { Uuid = Uuid, WafEnabled = true, WafSettings = null }));
        Assert.False(CorazaRules.IsActive(Space(s => { foreach (var c in CorazaRules.CategoryTags.Keys) s.Engine.Categories[c] = false; })));
        Assert.True(CorazaRules.IsActive(Space(s =>
        {
            foreach (var c in CorazaRules.CategoryTags.Keys) s.Engine.Categories[c] = false;
            s.CustomRules = [new WafCustomRule { Id = "r", Target = "path", Op = "equals", Value = "/x", Action = "block", Enabled = true }];
        })));
    }

    // ---- Caddyfile ----

    private static string Caddyfile(Action<WafSettings>? tweak = null, bool coraza = true)
    {
        CorazaProbe.Override = coraza;
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = "/var/lib/fq", Data = "/var/lib/fq/data", DiskLimiterMode = "none",
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy" },
            },
        };
        config.System.Quotas.Enabled = false;
        var space = new WebSpace
        {
            Uuid = Uuid, Runtime = "static", Domains = ["app.example.com"], Ssl = true, WafEnabled = true,
            WafSettings = Settings(tweak), CreatedAt = DateTimeOffset.UtcNow,
        };
        return new ReverseProxyManager(config).BuildConfig([space]).Replace("\r\n", "\n");
    }

    [Fact]
    public void Caddyfile_EmitsCorazaBlockAndGlobalOrder()
    {
        var text = Caddyfile();
        Assert.StartsWith("{\n\torder coraza_waf first\n}", text);
        Assert.Contains("coraza_waf {\n\t\tload_owasp_crs\n\t\tdirectives `\n\t\t\tInclude @coraza.conf-recommended\n\t\t\tInclude /var/lib/fq/proxy/waf/rules/", text);
        Assert.Contains("@fq_page expression `{http.error.message} in [\"fq-waf\", \"interruption triggered\"]`", text);
        Assert.Contains("handle_errors", text);
    }

    [Fact]
    public void Caddyfile_WithoutCorazaModule_IsUnchanged()
    {
        var text = Caddyfile(coraza: false);
        Assert.DoesNotContain("coraza", text);
        Assert.DoesNotContain("interruption", text);
    }

    [Fact]
    public void Caddyfile_SharedRuleSetsHaveTheSamePath()
    {
        var a = Caddyfile();
        var b = Caddyfile();
        Assert.Equal(System.Text.RegularExpressions.Regex.Match(a, "Include (/\\S+\\.conf)").Groups[1].Value,
            System.Text.RegularExpressions.Regex.Match(b, "Include (/\\S+\\.conf)").Groups[1].Value);
    }

    // ---- files ----

    [Fact]
    public void Sync_WritesSharedRulesAndPrunesOldOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-coraza-" + Guid.NewGuid().ToString("N"));
        try
        {
            WebSpace Space(string id, Action<WafSettings>? t = null) => new() { Uuid = Guid.Parse(id), WafEnabled = true, WafSettings = Settings(t) };
            var a = Space("aaaaaaaa-0000-0000-0000-000000000001");
            var b = Space("aaaaaaaa-0000-0000-0000-000000000002");
            var c = Space("aaaaaaaa-0000-0000-0000-000000000003", s => s.Engine.Sensitivity = 3);
            CorazaFiles.Sync(root, [a, b, c]);
            var files = Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf");
            Assert.Equal(2, files.Length);
            Assert.Empty(Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.tmp"));

            CorazaFiles.Sync(root, [a, b]);
            Assert.Single(Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sync_RuleSetRefusedByCoraza_FallsBackWithoutCustomRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-coraza-" + Guid.NewGuid().ToString("N"));
        try
        {
            CorazaFiles.Validator = file => !File.ReadAllText(file).Contains("refused-marker", StringComparison.Ordinal);
            var warnings = new List<string>();
            var space = new WebSpace
            {
                Uuid = Uuid, WafEnabled = true,
                WafSettings = Settings(s => s.CustomRules = [new WafCustomRule { Id = "r1", Target = "path", Op = "contains", Value = "refused-marker", Action = "block", Enabled = true }]),
            };
            CorazaFiles.Sync(root, [space], warnings.Add);
            Assert.Single(warnings);
            var files = Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf");
            Assert.Single(files);
            Assert.DoesNotContain("refused-marker", File.ReadAllText(files[0]));
            Assert.Equal(files[0], CorazaFiles.PathFor(space.WafSettings!, root)!.Replace('/', Path.DirectorySeparatorChar));

            // Second pass: remembered, no new warning, same result.
            CorazaFiles.Sync(root, [space], warnings.Add);
            Assert.Single(warnings);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sync_DoesNothingWithoutModule()
    {
        CorazaProbe.Override = false;
        var root = Path.Combine(Path.GetTempPath(), "fq-coraza-" + Guid.NewGuid().ToString("N"));
        CorazaFiles.Sync(root, [new WebSpace { Uuid = Uuid, WafEnabled = true, WafSettings = Settings() }]);
        Assert.False(Directory.Exists(root));
    }

    // ---- OWASP rules on disk ----

    private static string MakeRoot() => Path.Combine(Path.GetTempPath(), "fq-crs-" + Guid.NewGuid().ToString("N"));

    private static void PutCrs(string root, string version = "4.30.0", bool complete = true)
    {
        var dir = CorazaFiles.CrsDirectory(root);
        Directory.CreateDirectory(Path.Combine(dir, "rules"));
        File.WriteAllText(Path.Combine(dir, "crs-setup.conf"), "# setup");
        if (complete)
            File.WriteAllText(Path.Combine(dir, "rules", "REQUEST-901-INITIALIZATION.conf"), "# rules");
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + "\n");
    }

    [Fact]
    public void Build_WithDiskCrs_IncludesItsFiles_NotTheEmbeddedOnes()
    {
        var crs = new CrsSource("/var/lib/fq/proxy/waf/crs", "4.30.0");
        var text = CorazaRules.Build(Settings(), Audit, crs: crs);
        Assert.Contains("# CRS 4.30.0 (on disk)", text);
        Assert.Contains("Include /var/lib/fq/proxy/waf/crs/crs-setup.conf", text);
        Assert.Contains("Include /var/lib/fq/proxy/waf/crs/rules/*.conf", text);
        Assert.DoesNotContain("@owasp_crs", text);
        Assert.DoesNotContain("@crs-setup", text);
        Assert.NotEqual(CorazaRules.Hash(text), CorazaRules.Hash(CorazaRules.Build(Settings(), Audit)));
        Assert.NotEqual(CorazaRules.Hash(text), CorazaRules.Hash(CorazaRules.Build(Settings(), Audit, crs: crs with { Version = "4.31.0" })));
    }

    [Fact]
    public void CurrentCrs_NeedsAllPartsAndASafeVersion()
    {
        var root = MakeRoot();
        try
        {
            Assert.Null(CorazaFiles.CurrentCrs(root));
            PutCrs(root, complete: false);
            Assert.Null(CorazaFiles.CurrentCrs(root));
            PutCrs(root);
            var crs = CorazaFiles.CurrentCrs(root)!;
            Assert.Equal("4.30.0", crs.Version);
            Assert.DoesNotContain('\\', crs.Directory);
            PutCrs(root, "4.30.0 Include /etc/hosts");
            Assert.Null(CorazaFiles.CurrentCrs(root));
            PutCrs(root, "../../x");
            Assert.Null(CorazaFiles.CurrentCrs(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sync_UsesDiskCrs_AndFallsBackToEmbeddedWhenCorazaRefusesIt()
    {
        var root = MakeRoot();
        try
        {
            PutCrs(root);
            var space = new WebSpace { Uuid = Uuid, WafEnabled = true, WafSettings = Settings() };
            CorazaFiles.Sync(root, [space]);
            var files = Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf");
            Assert.Single(files);
            Assert.Contains("(on disk)", File.ReadAllText(files[0]));

            // an updated CRS that Coraza rejects must not be used: the embedded rules take over
            PutCrs(root, "9.9.9");
            CorazaFiles.Validator = file => !File.ReadAllText(file).Contains("9.9.9", StringComparison.Ordinal);
            var warnings = new List<string>();
            CorazaFiles.Sync(root, [space], warnings.Add);
            Assert.Single(warnings);
            files = Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf");
            Assert.Single(files);
            var text = File.ReadAllText(files[0]);
            Assert.Contains("@owasp_crs", text);
            Assert.DoesNotContain("9.9.9", text);
            Assert.Equal(files[0], CorazaFiles.PathFor(space.WafSettings!, root)!.Replace('/', Path.DirectorySeparatorChar));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void CrsChanged_ReportsOnlyRealChanges()
    {
        var root = MakeRoot();
        try
        {
            PutCrs(root, "4.30.0");
            Assert.False(CorazaFiles.CrsChanged(root)); // first call only records
            Assert.False(CorazaFiles.CrsChanged(root));
            PutCrs(root, "4.31.0");
            Assert.True(CorazaFiles.CrsChanged(root));
            Assert.False(CorazaFiles.CrsChanged(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Build_ExcludesCookiesFromSqliRules_OnlyWhileSqliIsOn()
    {
        var on = CorazaRules.Build(Settings(), Audit);
        Assert.Contains("SecRuleUpdateTargetByTag \"attack-sqli\" \"!REQUEST_COOKIES:/^(ph_phc_", on);
        Assert.Contains("SecRuleUpdateTargetByTag \"attack-sqli\" \"!REQUEST_COOKIES_NAMES:/^(ph_phc_", on);
        // never the blanket form: ordinary cookies such as a session id must still be inspected
        Assert.DoesNotContain("\"!REQUEST_COOKIES\"", on);
        Assert.DoesNotContain("\"!REQUEST_COOKIES_NAMES\"", on);
        // after the rules are loaded, otherwise there is nothing to update
        Assert.True(on.IndexOf("Include @owasp_crs", StringComparison.Ordinal) < on.IndexOf("SecRuleUpdateTargetByTag", StringComparison.Ordinal));

        var off = CorazaRules.Build(Settings(s => s.Engine.Categories["sqli"] = false), Audit);
        Assert.DoesNotContain("SecRuleUpdateTargetByTag", off);
        Assert.Contains("SecRuleRemoveByTag \"attack-sqli\"", off);
    }

    [Theory]
    [InlineData("ph_phc_Tbfg4EiRsr5iefFoth2Y1Hi3sttTeLQ5RV5TLg4hL1W_posthog", true)]
    [InlineData("_ga", true)]
    [InlineData("_ga_ABC123XYZ", true)]
    [InlineData("_gid", true)]
    [InlineData("_fbp", true)]
    [InlineData("__utmz", true)]
    [InlineData("wp-settings-time-1", true)]
    [InlineData("session", false)]
    [InlineData("PHPSESSID", false)]
    [InlineData("id", false)]
    [InlineData("user", false)]
    [InlineData("ph_x", false)]
    [InlineData("_ga; id", false)]
    [InlineData("my_ga", false)]
    [InlineData("xph_phc_abc_posthog", false)]
    public void HarmlessCookieNames_MatchOnlyKnownAnalyticsCookies(string name, bool expected)
    {
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.IsMatch(name, CorazaRules.HarmlessCookieNames));
    }

    [Fact]
    public void Build_ChecksClientAddressHeadersForSqli_OnlyWhileSqliIsOn()
    {
        var on = CorazaRules.Build(Settings(), Audit);
        Assert.Contains("id:9100100", on);
        Assert.Contains("REQUEST_HEADERS:X-Real-IP", on);
        Assert.Contains("@detectSQLi", on);
        // scored like the CRS rules, after the CRS is loaded so tx.critical_anomaly_score exists
        Assert.Contains("tx.inbound_anomaly_score_pl1=+%{tx.critical_anomaly_score}", on);
        Assert.True(on.IndexOf("Include @owasp_crs", StringComparison.Ordinal) < on.IndexOf("id:9100100", StringComparison.Ordinal));

        var off = CorazaRules.Build(Settings(s => s.Engine.Categories["sqli"] = false), Audit);
        Assert.DoesNotContain("id:9100100", off);
    }

    [Fact]
    public void Build_WordPressExclusions_OnlyForPhpAndWhenInstalled_InTheRightOrder()
    {
        var crs = new CrsSource("/var/lib/fq/proxy/waf/crs", "4.30.0", "1.2.0");
        var php = CorazaRules.Build(Settings(), Audit, crs: crs, php: true);
        var cfg = php.IndexOf("plugins/wordpress-rule-exclusions-config.conf", StringComparison.Ordinal);
        var action = php.IndexOf("id:900000", StringComparison.Ordinal);
        var before = php.IndexOf("plugins/wordpress-rule-exclusions-before.conf", StringComparison.Ordinal);
        var rules = php.IndexOf("crs/rules/*.conf", StringComparison.Ordinal);
        Assert.True(cfg > 0 && cfg < action && action < before && before < rules, "config, then paranoia level, then before-rules, then the CRS");
        Assert.Contains("# OWASP WordPress rule exclusions 1.2.0", php);

        // not for other runtimes, and not when the plugin is not installed
        Assert.DoesNotContain("wordpress-rule-exclusions", CorazaRules.Build(Settings(), Audit, crs: crs, php: false));
        Assert.DoesNotContain("wordpress-rule-exclusions", CorazaRules.Build(Settings(), Audit, crs: crs with { WordPress = null }, php: true));
        Assert.DoesNotContain("wordpress-rule-exclusions", CorazaRules.Build(Settings(), Audit, php: true));
        // a new plugin version means a new rule set
        Assert.NotEqual(CorazaRules.Hash(php), CorazaRules.Hash(CorazaRules.Build(Settings(), Audit, crs: crs with { WordPress = "1.3.0" }, php: true)));
    }

    [Fact]
    public void CurrentCrs_ReadsThePluginOnlyWhenAllPartsExist()
    {
        var root = MakeRoot();
        try
        {
            PutCrs(root);
            Assert.Null(CorazaFiles.CurrentCrs(root)!.WordPress);
            var plugins = Path.Combine(CorazaFiles.CrsDirectory(root), "plugins");
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, "wordpress-rule-exclusions-config.conf"), "#");
            File.WriteAllText(Path.Combine(plugins, "wordpress-rule-exclusions-before.conf"), "#");
            Assert.Null(CorazaFiles.CurrentCrs(root)!.WordPress);                       // no version file yet
            File.WriteAllText(Path.Combine(plugins, "wordpress.VERSION"), "1.2.0\n");
            Assert.Equal("1.2.0", CorazaFiles.CurrentCrs(root)!.WordPress);
            File.WriteAllText(Path.Combine(plugins, "wordpress.VERSION"), "1.2.0 Include /etc/hosts");
            Assert.Null(CorazaFiles.CurrentCrs(root)!.WordPress);                       // unsafe version text is ignored
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static void PutWordPress(string root)
    {
        PutCrs(root);
        var plugins = Path.Combine(CorazaFiles.CrsDirectory(root), "plugins");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "wordpress-rule-exclusions-config.conf"), "#");
        File.WriteAllText(Path.Combine(plugins, "wordpress-rule-exclusions-before.conf"), "#");
        File.WriteAllText(Path.Combine(plugins, "wordpress.VERSION"), "1.2.0\n");
    }

    [Fact]
    public void Sync_PhpSpaceGetsWordPressRules_OtherRuntimesDoNot()
    {
        var root = MakeRoot();
        try
        {
            PutWordPress(root);
            var space = new WebSpace { Uuid = Uuid, Runtime = "php", WafEnabled = true, WafSettings = Settings() };
            var staticSpace = new WebSpace { Uuid = Guid.NewGuid(), Runtime = "static", WafEnabled = true, WafSettings = Settings() };
            CorazaFiles.Sync(root, [space, staticSpace]);
            var texts = Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf").Select(File.ReadAllText).ToList();
            Assert.Equal(2, texts.Count);
            Assert.Single(texts, t => t.Contains("wordpress-rule-exclusions-before.conf", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sync_PhpSpace_FallsBackWhenCorazaRefusesTheWordPressRules()
    {
        var root = MakeRoot();
        try
        {
            PutWordPress(root);
            var space = new WebSpace { Uuid = Uuid, Runtime = "php", WafEnabled = true, WafSettings = Settings() };
            CorazaFiles.Validator = file => !File.ReadAllText(file).Contains("wordpress-rule-exclusions", StringComparison.Ordinal);
            var warnings = new List<string>();
            CorazaFiles.Sync(root, [space], warnings.Add);
            Assert.Single(warnings);
            var files = Directory.GetFiles(CorazaFiles.RulesDirectory(root), "*.conf");
            Assert.Single(files);
            var text = File.ReadAllText(files[0]);
            Assert.DoesNotContain("wordpress-rule-exclusions", text);
            Assert.Contains("(on disk)", text); // still the on-disk CRS, only without the WordPress file
        }
        finally
        {
            CorazaFiles.Validator = null;
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
