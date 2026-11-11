using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// Per-WebSpace layer-7 firewall settings pushed by the panel (<c>waf_settings</c>).
/// Deny IP/path lists travel in their own <c>waf_deny_*</c> fields.
/// Everything here is re-validated by <see cref="WafSettingsSanitizer"/> on the node: the daemon never trusts the panel.
/// </summary>
public sealed class WafSettings
{
    /// <summary>off | detect | block. Detect only logs attack-engine hits; explicit customer rules still apply.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "detect";
    [JsonPropertyName("allow_ips")] public List<string> AllowIps { get; set; } = [];
    [JsonPropertyName("rate_limit")] public WafRateLimit RateLimit { get; set; } = new();
    [JsonPropertyName("anti_bot")] public WafAntiBot AntiBot { get; set; } = new();
    [JsonPropertyName("exceptions")] public List<WafException> Exceptions { get; set; } = [];
    [JsonPropertyName("engine")] public WafEngine Engine { get; set; } = new();
    [JsonPropertyName("custom_rules")] public List<WafCustomRule> CustomRules { get; set; } = [];
    [JsonPropertyName("geo")] public WafGeo Geo { get; set; } = new();
    [JsonPropertyName("http")] public WafHttp Http { get; set; } = new();
    [JsonPropertyName("headers")] public WafHeaders Headers { get; set; } = new();
    [JsonPropertyName("hotlink")] public WafHotlink Hotlink { get; set; } = new();
    [JsonPropertyName("login")] public WafLogin Login { get; set; } = new();
    [JsonPropertyName("block_page")] public WafBlockPage BlockPage { get; set; } = new();
}

public sealed class WafRateLimit
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("per_minute")] public int PerMinute { get; set; } = 240;
    [JsonPropertyName("ban_minutes")] public int BanMinutes { get; set; } = 15;
}

public sealed class WafLogin
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = [];
    [JsonPropertyName("per_minute")] public int PerMinute { get; set; } = 10;
    [JsonPropertyName("ban_minutes")] public int BanMinutes { get; set; } = 30;
}

public sealed class WafAntiBot
{
    [JsonPropertyName("scanner_paths")] public bool ScannerPaths { get; set; } = true;
    [JsonPropertyName("bad_agents")] public bool BadAgents { get; set; }
    [JsonPropertyName("empty_agent")] public bool EmptyAgent { get; set; }
    [JsonPropertyName("custom_agents")] public List<string> CustomAgents { get; set; } = [];
}

public sealed class WafException
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
}

public sealed class WafEngine
{
    [JsonPropertyName("sensitivity")] public int Sensitivity { get; set; } = 1;
    [JsonPropertyName("categories")] public Dictionary<string, bool> Categories { get; set; } = new();
}

public sealed class WafCustomRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "path";
    [JsonPropertyName("op")] public string Op { get; set; } = "contains";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "block";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

public sealed class WafGeo
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
    [JsonPropertyName("countries")] public List<string> Countries { get; set; } = [];
}

public sealed class WafHttp
{
    [JsonPropertyName("methods")] public List<string> Methods { get; set; } = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
    [JsonPropertyName("max_body_mb")] public int MaxBodyMb { get; set; } = 10;
    [JsonPropertyName("max_url_length")] public int MaxUrlLength { get; set; } = 2048;
}

public sealed class WafHeaders
{
    [JsonPropertyName("hsts")] public bool Hsts { get; set; }
    [JsonPropertyName("nosniff")] public bool Nosniff { get; set; } = true;
    [JsonPropertyName("referrer")] public bool Referrer { get; set; } = true;
    [JsonPropertyName("permissions")] public bool Permissions { get; set; }
    [JsonPropertyName("frame")] public string Frame { get; set; } = "sameorigin";
}

public sealed class WafHotlink
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("allowed_hosts")] public List<string> AllowedHosts { get; set; } = [];
    [JsonPropertyName("extensions")] public List<string> Extensions { get; set; } = ["jpg", "jpeg", "png", "gif", "webp"];
}

public sealed class WafBlockPage
{
    [JsonPropertyName("status")] public int Status { get; set; } = 403;
    [JsonPropertyName("message")] public string Message { get; set; } = "Access denied";
    [JsonPropertyName("html")] public string Html { get; set; } = "";
}

/// <summary>
/// Node-side validation of <see cref="WafSettings"/>. Invalid entries are dropped or clamped, never passed on to the
/// proxy config generator, so a compromised or buggy panel cannot inject Caddyfile directives.
/// </summary>
public static class WafSettingsSanitizer
{
    public const int MaxHtmlBytes = 65536;
    private static readonly string[] Modes = ["off", "detect", "block"];
    private static readonly string[] Categories = ["sqli", "xss", "rce", "lfi", "php", "protocol", "scanner", "leakage"];
    private static readonly string[] Targets = ["path", "query", "user_agent", "referer", "method", "host"];
    private static readonly string[] Ops = ["contains", "equals", "starts", "regex"];
    private static readonly string[] Actions = ["block", "allow", "log"];
    private static readonly string[] Methods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
    private static readonly int[] BlockStatuses = [403, 406, 429, 503];
    private static readonly Regex HostRe = new(@"^(\*\.)?([a-z0-9-]+\.)+[a-z]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex ExtRe = new(@"^[a-z0-9]{1,5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex IdRe = new(@"^[A-Za-z0-9-]{1,40}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// True when the value can be written as one bare Caddyfile token: no whitespace, control characters, quotes,
    /// braces, backslashes or backticks (placeholders, comments and directive injection all need one of those).
    /// </summary>
    public static bool IsSafeToken(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            return false;
        foreach (var ch in value)
        {
            if (ch <= ' ' || ch == 0x7f || ch is '"' or '\'' or '`' or '{' or '}' or '\\' or '<' or '>')
                return false;
        }

        return true;
    }

    public static bool IsValidPath(string? path) => IsSafeToken(path) && path![0] == '/';

    public static WafSettings Sanitize(WafSettings? input)
    {
        var s = new WafSettings();
        if (input is null)
            return s;

        s.Mode = Modes.Contains(input.Mode) ? input.Mode : "detect";
        s.AllowIps = WebSpaceStore.SanitizeDenyIps(input.AllowIps);

        var rl = input.RateLimit ?? new WafRateLimit();
        s.RateLimit = new WafRateLimit { Enabled = rl.Enabled, PerMinute = Math.Clamp(rl.PerMinute, 10, 10000), BanMinutes = Math.Clamp(rl.BanMinutes, 1, 1440) };

        var lg = input.Login ?? new WafLogin();
        s.Login = new WafLogin
        {
            Enabled = lg.Enabled,
            PerMinute = Math.Clamp(lg.PerMinute, 1, 600),
            BanMinutes = Math.Clamp(lg.BanMinutes, 1, 1440),
            Paths = (lg.Paths ?? []).Where(IsValidPath).Distinct().Take(30).ToList(),
        };

        var ab = input.AntiBot ?? new WafAntiBot();
        s.AntiBot = new WafAntiBot
        {
            ScannerPaths = ab.ScannerPaths,
            BadAgents = ab.BadAgents,
            EmptyAgent = ab.EmptyAgent,
            CustomAgents = (ab.CustomAgents ?? []).Where(a => a is { Length: >= 2 and <= 80 } && !a.Any(c => c < ' ' || c == 0x7f)).Distinct().Take(50).ToList(),
        };

        s.Exceptions = (input.Exceptions ?? [])
            .Where(e => e is not null && IsValidPath(e.Path) && e.Path != "/")
            .GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.First()).Take(100)
            .Select(e => new WafException
            {
                Id = IdRe.IsMatch(e.Id ?? "") ? e.Id! : "",
                Path = e.Path,
                Categories = (e.Categories ?? []).Where(c => Categories.Contains(c)).Distinct().ToList(),
            }).ToList();

        var en = input.Engine ?? new WafEngine();
        s.Engine = new WafEngine { Sensitivity = Math.Clamp(en.Sensitivity, 1, 4) };
        foreach (var c in Categories)
            s.Engine.Categories[c] = en.Categories is not null && en.Categories.TryGetValue(c, out var on) ? on : c != "leakage";

        s.CustomRules = (input.CustomRules ?? [])
            .Where(r => r is not null && Targets.Contains(r.Target) && Ops.Contains(r.Op) && Actions.Contains(r.Action)
                        && !string.IsNullOrEmpty(r.Value) && r.Value.Length <= 200 && !r.Value.Any(c => c < ' ' && c != '\t' || c == 0x7f))
            .Take(30)
            .Select(r => new WafCustomRule
            {
                Id = IdRe.IsMatch(r.Id ?? "") ? r.Id! : "",
                Name = (r.Name ?? "").Length > 60 ? r.Name![..60] : r.Name ?? "",
                Target = r.Target, Op = r.Op, Value = r.Value, Action = r.Action, Enabled = r.Enabled,
            }).ToList();

        var geo = input.Geo ?? new WafGeo();
        s.Geo = new WafGeo
        {
            Mode = geo.Mode is "off" or "deny" or "allow" ? geo.Mode : "off",
            Countries = (geo.Countries ?? []).Where(c => c is { Length: 2 } && c.All(ch => ch is >= 'A' and <= 'Z')).Distinct().Take(250).ToList(),
        };

        var http = input.Http ?? new WafHttp();
        var methods = (http.Methods ?? []).Where(m => Methods.Contains(m)).Distinct().ToList();
        s.Http = new WafHttp
        {
            Methods = methods.Count > 0 ? methods : [.. Methods],
            MaxBodyMb = Math.Clamp(http.MaxBodyMb, 1, 1024),
            MaxUrlLength = Math.Clamp(http.MaxUrlLength, 256, 8192),
        };

        var h = input.Headers ?? new WafHeaders();
        s.Headers = new WafHeaders
        {
            Hsts = h.Hsts, Nosniff = h.Nosniff, Referrer = h.Referrer, Permissions = h.Permissions,
            Frame = h.Frame is "off" or "sameorigin" or "deny" ? h.Frame : "sameorigin",
        };

        var hot = input.Hotlink ?? new WafHotlink();
        s.Hotlink = new WafHotlink
        {
            Enabled = hot.Enabled,
            AllowedHosts = (hot.AllowedHosts ?? []).Where(x => x is { Length: <= 253 } && HostRe.IsMatch(x)).Select(x => x.ToLowerInvariant()).Distinct().Take(50).ToList(),
            Extensions = (hot.Extensions ?? []).Where(x => x is not null && ExtRe.IsMatch(x)).Select(x => x.ToLowerInvariant()).Distinct().Take(30).ToList(),
        };

        var bp = input.BlockPage ?? new WafBlockPage();
        var html = bp.Html ?? "";
        s.BlockPage = new WafBlockPage
        {
            Status = BlockStatuses.Contains(bp.Status) ? bp.Status : 403,
            Message = (bp.Message ?? "").Length > 300 ? bp.Message![..300] : bp.Message ?? "",
            Html = System.Text.Encoding.UTF8.GetByteCount(html) <= MaxHtmlBytes ? html : "",
        };

        return s;
    }
}
