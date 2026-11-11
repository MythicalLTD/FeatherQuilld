using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>An OWASP Core Rule Set unpacked on disk: directory (forward slashes) and its version.</summary>
public sealed record CrsSource(string Directory, string Version, string? WordPress = null);

/// <summary>Detects whether the installed Caddy was built with the Coraza module (<c>http.handlers.waf</c>).</summary>
public static class CorazaProbe
{
    /// <summary>Tests can force the answer.</summary>
    internal static bool? Override { get; set; }

    /// <summary>Tests can force the rate-limit answer.</summary>
    internal static bool? RateLimitOverride { get; set; }

    private static bool _rate;

    private static readonly object Gate = new();
    private static bool _value;
    private static DateTime _checkedAt = DateTime.MinValue;

    public static bool IsAvailable()
    {
        if (Override is { } forced)
            return forced;

        lock (Gate)
        {
            // Re-check now and then so a Caddy upgrade is picked up without restarting the daemon.
            if (DateTime.UtcNow - _checkedAt < TimeSpan.FromMinutes(5))
                return _value;

            _value = Probe();
            _checkedAt = DateTime.UtcNow;
            return _value;
        }
    }

    /// <summary>True when this Caddy has the rate-limit module (<c>http.handlers.rate_limit</c>).</summary>
    public static bool RateLimitAvailable()
    {
        if (RateLimitOverride is { } forced)
            return forced;
        _ = IsAvailable(); // refreshes the cached module list when it is old
        lock (Gate)
        {
            return _rate;
        }
    }

    private static bool Probe()
    {
        _rate = false;
        var binary = ProxyProbe.ResolveBinary("caddy");
        if (binary is null)
            return false;
        var (ok, output) = CorazaFiles.Run(binary, ["list-modules"], 15);
        _rate = ok && output.Contains("http.handlers.rate_limit", StringComparison.Ordinal);
        return ok && output.Contains("http.handlers.waf", StringComparison.Ordinal);
    }
}

/// <summary>
/// Translates <see cref="WafSettings"/> into a Coraza (SecLang) rule file: OWASP CRS tuning, customer rules, exceptions.
///
/// The file is content-addressed (<c>rules/&lt;hash&gt;.conf</c>), so WebSpaces with identical settings share one rule set
/// inside Caddy instead of each loading its own copy of the ~900 CRS rules. The audit log is one node-wide file because of that;
/// the JSON entries carry the host, so statistics are split per WebSpace later.
///
/// Monitor mode keeps the engine on for the customer's own explicit deny rules, then switches the transaction to DetectionOnly
/// before the CRS runs: CRS hits are only reported. Nothing customer-controlled reaches the file unescaped: values are
/// rejected when they contain quotes, macros (<c>%{</c>), control characters or a trailing backslash.
/// </summary>
public static class CorazaRules
{
    private const int FirstCustomId = 100100;
    private const int AllowIpId = 110100;
    private const int MonitorId = 110900;
    private const int FirstExceptionId = 110200;

    /// <summary>Which CRS rule tags belong to each category shown in the panel.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> CategoryTags = new Dictionary<string, string[]>
    {
        ["sqli"] = ["attack-sqli"],
        ["xss"] = ["attack-xss"],
        ["rce"] = ["attack-rce", "attack-injection-generic", "attack-injection-java", "attack-ssrf", "attack-ssti"],
        ["lfi"] = ["attack-lfi", "attack-rfi"],
        ["php"] = ["attack-injection-php"],
        ["protocol"] = ["attack-protocol"],
        ["scanner"] = ["attack-reputation-scanner"],
        ["leakage"] = ["attack-disclosure"],
    };

    private static readonly string[] AllCategories = [.. CategoryTags.Keys];

    /// <summary>True when the attack engine (CRS) is needed: at least one category is on.</summary>
    public static bool UsesCrs(WafSettings s) => AllCategories.Any(c => s.Engine.Categories.TryGetValue(c, out var on) && on);

    /// <summary>Whether this WebSpace needs a Coraza instance at all.</summary>
    public static bool IsActive(WebSpace space)
    {
        if (!space.WafEnabled || space.WafSettings is null)
            return false;
        var s = WafSettingsSanitizer.Sanitize(space.WafSettings);
        return s.Mode != "off" && (UsesCrs(s) || RenderCustomRules(s).Count > 0);
    }

    /// <summary>
    /// <paramref name="php"/>: PHP WebSpace, so the OWASP WordPress rule exclusions (when installed next to the CRS) are loaded. They only
    /// match WordPress's own admin, login, REST and comment paths and remove the false positives that block editing posts.
    /// <paramref name="crs"/>: an OWASP CRS unpacked on disk (see <see cref="CorazaFiles.CurrentCrs"/>); null uses the copy built into Caddy.
    /// The on-disk copy lets the rules be updated without replacing Caddy.
    /// </summary>
    /// <summary>Names of widely used analytics/consent cookies (anchored regex, no user data in them).</summary>
    internal const string HarmlessCookieNames =
        "^(ph_phc_[A-Za-z0-9]+_posthog|_ga(_[A-Za-z0-9]+)?|_gid|_gat(_[A-Za-z0-9_]+)?|_gcl_(au|aw|dc|gb|gs|ag)|_fbp|_fbc" +
        "|__utm[a-z]{1,3}|_hj[A-Za-z]+(_[0-9]+)?|__cf_bm|cf_clearance|wp-settings-(time-)?[0-9]+|wordpress_test_cookie)$";

    /// <summary>SQL injection in the request headers that apps commonly trust for the visitor's address.</summary>
    internal const string ClientAddressHeaderRule =
        "SecRule REQUEST_HEADERS:X-Forwarded-For|REQUEST_HEADERS:X-Real-IP|REQUEST_HEADERS:Client-IP|REQUEST_HEADERS:True-Client-IP" +
        "|REQUEST_HEADERS:X-Client-IP|REQUEST_HEADERS:X-Originating-IP|REQUEST_HEADERS:X-Forwarded-Host|REQUEST_HEADERS:CF-Connecting-IP " +
        "\"@detectSQLi\" \"id:9100100,phase:1,block,capture,t:none,t:urlDecodeUni," +
        "msg:'SQL injection in a client-address header',logdata:'Matched Data: %{TX.0} found within %{MATCHED_VAR_NAME}'," +
        "tag:'attack-sqli',tag:'OWASP_CRS',severity:'CRITICAL'," +
        "setvar:'tx.inbound_anomaly_score_pl1=+%{tx.critical_anomaly_score}',setvar:'tx.sql_injection_score=+%{tx.critical_anomaly_score}'\"";

    public static string Build(WafSettings s, string auditLogPath, bool includeCustomRules = true, CrsSource? crs = null, bool php = false)
    {
        var useCrs = UsesCrs(s);
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by FeatherQuilld - do not edit by hand");
        sb.AppendLine("SecRuleEngine On");
        sb.AppendLine("SecRequestBodyAccess On");
        sb.AppendLine("SecRequestBodyLimit 13107200");
        sb.AppendLine("SecRequestBodyInMemoryLimit 131072");
        sb.AppendLine("SecRequestBodyNoFilesLimit 131072");
        sb.AppendLine("SecRequestBodyLimitAction ProcessPartial");
        var leakage = s.Engine.Categories.TryGetValue("leakage", out var l) && l;
        if (leakage)
        {
            sb.AppendLine("SecResponseBodyAccess On");
            sb.AppendLine("SecResponseBodyMimeType text/plain text/html text/xml application/json");
            sb.AppendLine("SecResponseBodyLimit 524288");
            sb.AppendLine("SecResponseBodyLimitAction ProcessPartial");
        }
        else
        {
            sb.AppendLine("SecResponseBodyAccess Off");
        }

        sb.AppendLine("SecAuditEngine RelevantOnly");
        sb.AppendLine("SecAuditLogFormat JSON");
        sb.AppendLine("SecAuditLogType Serial");
        sb.AppendLine($"SecAuditLog {auditLogPath}");
        sb.AppendLine("SecAuditLogParts AHZ");
        sb.AppendLine("SecAuditLogRelevantStatus \"^(?:(5|4)(0|1|3)\\d)$\"");

        if (useCrs)
        {
            sb.AppendLine(crs is null ? "Include @crs-setup.conf.example" : $"# CRS {crs.Version} (on disk)");
            var wp = crs?.WordPress is not null && php;
            if (crs is not null)
                sb.AppendLine($"Include {crs.Directory}/crs-setup.conf");
            if (wp)
            {
                sb.AppendLine($"# OWASP WordPress rule exclusions {crs!.WordPress}");
                sb.AppendLine($"Include {crs.Directory}/plugins/wordpress-rule-exclusions-config.conf");
            }
            var level = Math.Clamp(s.Engine.Sensitivity, 1, 4);
            // The CRS only lets GET, HEAD, POST and OPTIONS through (rule 911100). The customer's own method list decides instead,
            // otherwise PUT/PATCH/DELETE of every REST API on the site is refused although the dashboard says they are allowed.
            var methods = string.Join(' ', (s.Http?.Methods ?? []).Where(m => m.All(char.IsAsciiLetterUpper)).Distinct());
            var setvars = $"setvar:tx.blocking_paranoia_level={level},setvar:tx.detection_paranoia_level={level}";
            if (methods.Length > 0)
                setvars += $",setvar:'tx.allowed_methods={methods}'";
            sb.AppendLine($"SecAction \"id:900000,phase:1,nolog,pass,t:none,{setvars}\"");
        }

        if (s.AllowIps.Count > 0)
        {
            var ips = string.Join(',', WebSpaceStore.SanitizeDenyIps(s.AllowIps));
            if (ips.Length > 0)
                sb.AppendLine($"SecRule REMOTE_ADDR \"@ipMatch {ips}\" \"id:{AllowIpId},phase:1,pass,nolog,ctl:ruleEngine=Off\"");
        }

        // Customer rules run before the exceptions, so a path exception ("skip the attack checks here") never switches them off.
        if (includeCustomRules)
        {
            foreach (var line in RenderCustomRules(s))
                sb.AppendLine(line);
        }

        // Monitor mode: from here on only detect. Customer rules above already ran with the engine On, so their blocks still apply;
        // the CRS below only reports what it would have stopped (and RelevantOnly audit logging records exactly those requests).
        if (useCrs && s.Mode != "block")
            sb.AppendLine($"SecAction \"id:{MonitorId},phase:1,pass,nolog,ctl:ruleEngine=DetectionOnly\"");

        if (useCrs)
        {
            var id = FirstExceptionId;
            foreach (var ex in s.Exceptions)
            {
                var path = ex.Path.TrimEnd('/');
                if (path.Length == 0)
                    continue;
                var ctl = ex.Categories.Count == 0
                    ? "ctl:ruleEngine=Off"
                    : string.Join(',', ex.Categories.Where(CategoryTags.ContainsKey).SelectMany(c => CategoryTags[c]).Distinct().Select(t => $"ctl:ruleRemoveByTag={t}"));
                if (ctl.Length == 0)
                    continue;
                sb.AppendLine($"SecRule REQUEST_FILENAME \"@rx ^{RxEscape(path)}(/|$)\" \"id:{id++},phase:1,pass,nolog,t:urlDecodeUni,t:normalizePath,{ctl}\"");
            }

            if (crs?.WordPress is not null && php)
                sb.AppendLine($"Include {crs.Directory}/plugins/wordpress-rule-exclusions-before.conf");
            sb.AppendLine(crs is null ? "Include @owasp_crs/*.conf" : $"Include {crs.Directory}/rules/*.conf");

            if (s.Engine.Categories.TryGetValue("sqli", out var sqliOn) && sqliOn)
            {
                // Analytics/consent cookies carry JSON or base64 blobs that set off several SQL injection rules at paranoia level 2+
                // and locked real visitors out. Exactly those well-known cookie names are skipped by the SQL rules; every other
                // cookie (session, id, user data, ...) is still inspected, and other categories inspect all cookies.
                sb.AppendLine($"SecRuleUpdateTargetByTag \"attack-sqli\" \"!REQUEST_COOKIES:/{HarmlessCookieNames}/\"");
                sb.AppendLine($"SecRuleUpdateTargetByTag \"attack-sqli\" \"!REQUEST_COOKIES_NAMES:/{HarmlessCookieNames}/\"");

                // The CRS does not look at the headers that applications like to trust for the client address. Caddy rewrites
                // X-Forwarded-For before the app sees it, but the others are passed on as sent.
                sb.AppendLine(ClientAddressHeaderRule);
            }

            foreach (var cat in AllCategories)
            {
                if (s.Engine.Categories.TryGetValue(cat, out var on) && on)
                    continue;
                foreach (var tag in CategoryTags[cat])
                    sb.AppendLine($"SecRuleRemoveByTag \"{tag}\"");
            }

            if (s.Mode == "block" && s.BlockPage.Status != 403)
                sb.AppendLine($"SecRuleUpdateActionById 949110 \"deny,status:{s.BlockPage.Status}\"");
        }

        return sb.ToString().Replace("\r\n", "\n");
    }

    public static string Hash(string rules) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rules))).ToLowerInvariant()[..16];

    internal static List<string> RenderCustomRules(WafSettings s)
    {
        var lines = new List<string>();
        var id = FirstCustomId;
        foreach (var r in s.CustomRules.Where(r => r.Enabled))
        {
            var line = RenderCustomRule(r, id, s.BlockPage.Status);
            id++;
            if (line is not null)
                lines.Add(line);
        }

        return lines;
    }

    private static string? RenderCustomRule(WafCustomRule r, int id, int status)
    {
        var value = r.Value ?? "";
        if (value.Length == 0 || value.EndsWith('\\') || value.Contains("%{", StringComparison.Ordinal) ||
            value.Any(c => c < ' ' || c == 0x7f || c == '"'))
            return null;

        var caseInsensitive = r.Target is "user_agent" or "referer" or "host" or "method";
        var transforms = r.Target switch
        {
            "path" => "t:urlDecodeUni,t:normalizePath",
            "query" => "t:urlDecodeUni",
            _ => "t:lowercase",
        };
        if (r.Target == "method")
            value = value.ToUpperInvariant();

        string op;
        switch (r.Op)
        {
            case "contains": op = "@contains"; break;
            case "equals": op = "@streq"; break;
            case "starts": op = "@beginsWith"; break;
            case "regex":
                op = "@rx";
                try
                {
                    _ = new Regex(value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                catch (ArgumentException)
                {
                    return null;
                }

                break;
            default: return null;
        }

        // Case-insensitive targets: plain operators compare lower-cased on both sides; a regex keeps its subject and gets the
        // inline flag instead (lower-casing the pattern would turn \S, \D, \W into different classes).
        if (r.Target == "method")
        {
            transforms = "t:none";
        }
        else if (caseInsensitive)
        {
            if (r.Op == "regex")
            {
                value = "(?i)" + value;
                transforms = "t:none";
            }
            else
            {
                value = value.ToLowerInvariant();
            }
        }

        var target = r.Target switch
        {
            "path" => "REQUEST_FILENAME",
            "query" => "QUERY_STRING",
            "user_agent" => "REQUEST_HEADERS:User-Agent",
            "referer" => "REQUEST_HEADERS:Referer",
            "method" => "REQUEST_METHOD",
            "host" => "REQUEST_HEADERS:Host",
            _ => null,
        };
        if (target is null)
            return null;

        var name = r.Id.Length > 0 ? r.Id : id.ToString();
        var action = r.Action switch
        {
            "block" => $"deny,status:{status},log,auditlog,msg:'Custom rule {name}',tag:'fq-custom'",
            "log" => $"pass,log,auditlog,msg:'Custom rule {name}',tag:'fq-custom'",
            "allow" => "allow,nolog",
            _ => null,
        };
        if (action is null)
            return null;

        return $"SecRule {target} \"{op} {value}\" \"id:{id},phase:1,{transforms},{action}\"";
    }

    /// <summary>Escape a literal path for use inside a Go (RE2) regular expression.</summary>
    internal static string RxEscape(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        foreach (var c in path)
        {
            if (c < 128 && !char.IsLetterOrDigit(c) && c is not ('_' or '/' or '-'))
                sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>Files for the Coraza integration: rule sets, the shared audit log, and a safety check before a rule set goes live.</summary>
public static class CorazaFiles
{
    public static string BaseDirectory(string rootDirectory) => Path.Combine(rootDirectory, "proxy", "waf");
    public static string RulesDirectory(string rootDirectory) => Path.Combine(BaseDirectory(rootDirectory), "rules");
    public static string AuditLogPath(string rootDirectory) => Path.Combine(BaseDirectory(rootDirectory), "audit.json");
    public static string RulesPath(string rootDirectory, string hash) => Path.Combine(RulesDirectory(rootDirectory), hash + ".conf");

    /// <summary>Tests can replace the validation (normally <c>caddy validate</c> on a one-site config).</summary>
    internal static Func<string, bool>? Validator { get; set; }

    private static readonly HashSet<string> Rejected = [];
    private static readonly HashSet<string> Accepted = [];

    public static string CrsDirectory(string rootDirectory) => Path.Combine(BaseDirectory(rootDirectory), "crs");

    private static readonly System.Text.RegularExpressions.Regex VersionRe =
        new(@"^[0-9A-Za-z][0-9A-Za-z.\-]{0,31}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// An OWASP Core Rule Set unpacked in <c>proxy/waf/crs</c> (<c>crs-setup.conf</c>, <c>rules/</c>, <c>VERSION</c>), or null.
    /// It takes precedence over the copy built into Caddy, so the rules can be updated without replacing Caddy.
    /// </summary>
    public static CrsSource? CurrentCrs(string rootDirectory)
    {
        try
        {
            var dir = CrsDirectory(rootDirectory);
            if (!File.Exists(Path.Combine(dir, "crs-setup.conf")) ||
                !File.Exists(Path.Combine(dir, "rules", "REQUEST-901-INITIALIZATION.conf")) ||
                !File.Exists(Path.Combine(dir, "VERSION")))
                return null;
            var version = (File.ReadLines(Path.Combine(dir, "VERSION")).FirstOrDefault() ?? "").Trim();
            var normal = dir.Replace('\\', '/');
            string? wp = null;
            var wpBefore = Path.Combine(dir, "plugins", "wordpress-rule-exclusions-before.conf");
            var wpConfig = Path.Combine(dir, "plugins", "wordpress-rule-exclusions-config.conf");
            var wpVersion = Path.Combine(dir, "plugins", "wordpress.VERSION");
            if (File.Exists(wpBefore) && File.Exists(wpConfig) && File.Exists(wpVersion))
            {
                var v = (File.ReadLines(wpVersion).FirstOrDefault() ?? "").Trim();
                if (VersionRe.IsMatch(v))
                    wp = v;
            }

            return VersionRe.IsMatch(version) && WafSettingsSanitizer.IsSafeToken(normal, 380) ? new CrsSource(normal, version, wp) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The rule texts a WebSpace may use, best first: everything with the on-disk CRS, then without the customer rules, then the
    /// same two with the CRS built into Caddy. The first one Coraza accepts is used, so a broken customer rule or a broken CRS
    /// update can never stop the node from reloading.
    /// </summary>
    internal static List<string> Candidates(WafSettings s, string rootDirectory, bool php = false)
    {
        var audit = AuditLogPath(rootDirectory);
        var disk = CurrentCrs(rootDirectory);
        var hasCustom = CorazaRules.RenderCustomRules(s).Count > 0;
        var list = new List<string>();
        if (disk is not null)
        {
            list.Add(CorazaRules.Build(s, audit, crs: disk, php: php));
            if (hasCustom)
                list.Add(CorazaRules.Build(s, audit, includeCustomRules: false, crs: disk, php: php));
            // without the WordPress exclusions, should only those be the problem
            if (php && disk.WordPress is not null)
                list.Add(CorazaRules.Build(s, audit, crs: disk, php: false));
        }

        list.Add(CorazaRules.Build(s, audit));
        if (hasCustom)
            list.Add(CorazaRules.Build(s, audit, includeCustomRules: false));
        return list.DistinctBy(CorazaRules.Hash).ToList();
    }

    /// <summary>
    /// The rule text to use for a WebSpace: the first candidate Coraza has not refused. Deterministic given the sets that
    /// failed validation earlier, so the Caddyfile generator and the file writer agree.
    /// </summary>
    public static string RulesFor(WafSettings s, string rootDirectory, bool php = false)
    {
        var candidates = Candidates(s, rootDirectory, php);
        lock (Rejected)
        {
            return candidates.FirstOrDefault(c => !Rejected.Contains(CorazaRules.Hash(c))) ?? candidates[^1];
        }
    }

    public static string? PathFor(WafSettings s, string rootDirectory, bool php = false)
    {
        var path = RulesPath(rootDirectory, CorazaRules.Hash(RulesFor(s, rootDirectory, php)));
        return WafSettingsSanitizer.IsSafeToken(path, 400) && WafSettingsSanitizer.IsSafeToken(AuditLogPath(rootDirectory), 400) ? path : null;
    }

    /// <summary>
    /// Write every rule set the given WebSpaces need (new ones are checked with Coraza first) and delete the unused ones.
    /// A rule set Coraza refuses is never used; the next candidate is tried instead.
    /// </summary>
    public static void Sync(string rootDirectory, IEnumerable<WebSpace> spaces, Action<string>? warn = null)
    {
        if (!CorazaProbe.IsAvailable())
            return;
        try
        {
            Directory.CreateDirectory(RulesDirectory(rootDirectory));
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var space in spaces.Where(CorazaRules.IsActive))
            {
                var s = WafSettingsSanitizer.Sanitize(space.WafSettings);
                var php = string.Equals(space.Runtime, "php", StringComparison.OrdinalIgnoreCase);
                foreach (var rules in Candidates(s, rootDirectory, php))
                {
                    var hash = CorazaRules.Hash(rules);
                    if (Ensure(rootDirectory, hash, rules, warn))
                    {
                        keep.Add(hash);
                        break;
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(RulesDirectory(rootDirectory), "*.conf"))
            {
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"Coraza rule files could not be written: {ex.Message}");
        }
    }

    private static string? _lastCrsVersion;

    /// <summary>True once when the on-disk CRS changed since the last call (first call only records it).</summary>
    public static bool CrsChanged(string rootDirectory)
    {
        var now = (CurrentCrs(rootDirectory)?.Version ?? "") + "|" + WafGeoData.Stamp(rootDirectory);
        lock (Rejected)
        {
            var changed = _lastCrsVersion is not null && _lastCrsVersion != now;
            _lastCrsVersion = now;
            return changed;
        }
    }

    /// <summary>Largest the node-wide audit log may get before it is emptied (the WebSpaces have already taken over their entries).</summary>
    public const long MaxAuditBytes = 32L * 1024 * 1024;

    public static void RotateAuditLog(string rootDirectory, long maxBytes = MaxAuditBytes)
    {
        var path = AuditLogPath(rootDirectory);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maxBytes)
                return;
            // Truncate in place: Caddy keeps the file open for appending, so it simply continues at the new end.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.SetLength(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // try again at the next retention run
        }
    }

    private static bool Ensure(string rootDirectory, string hash, string rules, Action<string>? warn)
    {
        lock (Rejected)
        {
            if (Rejected.Contains(hash))
                return false;
            var target = RulesPath(rootDirectory, hash);
            if (Accepted.Contains(hash) && File.Exists(target))
                return true;

            var tmp = target + ".tmp";
            File.WriteAllText(tmp, rules, new UTF8Encoding(false));
            if (!Validate(rootDirectory, tmp))
            {
                File.Delete(tmp);
                Rejected.Add(hash);
                warn?.Invoke($"Coraza rejected rule set {hash}; falling back to a simpler rule set for the WebSpaces using it");
                return false;
            }

            File.Move(tmp, target, overwrite: true);
            Accepted.Add(hash);
            return true;
        }
    }

    private static bool Validate(string rootDirectory, string rulesFile)
    {
        if (Validator is { } custom)
            return custom(rulesFile);

        var binary = ProxyProbe.ResolveBinary("caddy");
        if (binary is null)
            return false;
        var dir = Path.Combine(BaseDirectory(rootDirectory), "check");
        Directory.CreateDirectory(dir);
        var caddyfile = Path.Combine(dir, "Caddyfile");
        File.WriteAllText(caddyfile,
            "{\n\torder coraza_waf first\n\tadmin off\n\tauto_https off\n}\n:65535 {\n\tcoraza_waf {\n\t\tload_owasp_crs\n\t\tdirectives `\n" +
            $"\t\t\tInclude @coraza.conf-recommended\n\t\t\tInclude {rulesFile}\n\t\t`\n\t}}\n\trespond \"ok\"\n}}\n");
        var (ok, _) = Run(binary, ["validate", "--config", caddyfile, "--adapter", "caddyfile"], 120);
        return ok;
    }

    internal static (bool ok, string output) Run(string file, string[] args, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = file, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "timeout");
            }

            return (proc.ExitCode == 0, stdout.Result + stderr.Result);
        }
        catch
        {
            return (false, "");
        }
    }
}
