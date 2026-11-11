using System.Text;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Layer-7 firewall directives for one Caddy site block, built from the panel's per-WebSpace settings.
///
/// Scope of this builder: everything Caddy can do on its own (allow/deny lists, methods, size limits, headers, hotlink
/// protection, anti-bot, block page, country filter from address-range files). Settings that need extra modules are emitted only
/// when this Caddy has them: attack engine, custom rules and exceptions (Coraza), rate limit and login limit (caddy-ratelimit).
///
/// Monitor ("detect") mode: the customer's own allow/deny lists, limits, hotlink rules and headers still apply; the built-in
/// anti-bot protections, country filter and rate limits only act in Block mode (they cannot merely report).
///
/// Every value comes from <see cref="WafSettingsSanitizer"/> and is additionally checked with
/// <see cref="WafSettingsSanitizer.IsSafeToken"/> before it is written, so no entry can add a Caddyfile directive.
/// </summary>
internal static class CaddyWafBuilder
{
    private const string ErrorMessage = "fq-waf";

    private static readonly string[] ScannerPaths =
    [
        "/.env*", "/.git", "/.git/*", "/.svn/*", "/.hg/*", "/.aws/*", "/.ssh/*", "/.DS_Store", "/.htpasswd", "/.htaccess",
        "/wp-config.php.*", "/wp-config.php~", "/wp-config.bak", "/*.sql", "/*.bak", "/*.old", "/*.swp", "/phpinfo.php", "/info.php",
        "/server-status", "/vendor/phpunit/*", "/backup.zip", "/backup.tar.gz", "/dump.sql", "/database.sql",
    ];

    /// <summary>Files a page pulls in by itself; the general rate limit counts page requests, not these.</summary>
    private static readonly string[] AssetPatterns =
    [
        "*.css", "*.js", "*.mjs", "*.map", "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.avif", "*.svg", "*.ico",
        "*.woff", "*.woff2", "*.ttf", "*.otf", "*.eot", "*.mp4", "*.webm", "*.mp3", "*.ogg", "*.wav",
    ];

    private const string BadAgentPattern =
        "(?i)(sqlmap|nikto|nmap|masscan|acunetix|nessus|openvas|wpscan|dirbuster|gobuster|havij|zgrab|nuclei|fimap|w3af|arachni|metasploit|jaeles)";

    public static void Append(StringBuilder sb, WebSpace space, IReadOnlyList<string> domains, string rootDirectory)
    {
        if (space.WafSettings is null)
            return;

        // Settings normally arrive sanitized from the store; check again so this generator is safe on its own.
        var s = WafSettingsSanitizer.Sanitize(space.WafSettings);
        if (s.Mode == "off")
            return;

        var block = s.Mode == "block";
        var status = s.BlockPage.Status;
        var pageDir = WafBlockPageFiles.Directory(rootDirectory, space.Uuid);
        var customPage = WafSettingsSanitizer.IsSafeToken(pageDir, 400);
        var usedErrors = false;
        var limited = false;

        string Deny(string matcher, int code)
        {
            if (customPage)
            {
                usedErrors = true;
                return $"\terror {matcher} \"{ErrorMessage}\" {code}";
            }

            return $"\trespond {matcher} \"Access denied\" {code}";
        }

        // Attack engine (Coraza + OWASP CRS), only when this Caddy was built with it.
        var corazaPath = CorazaProbe.IsAvailable() && CorazaRules.IsActive(space) ? CorazaFiles.PathFor(s, rootDirectory, string.Equals(space.Runtime, "php", StringComparison.OrdinalIgnoreCase)) : null;
        if (corazaPath is not null)
        {
            sb.AppendLine("\tcoraza_waf {");
            sb.AppendLine("\t\tload_owasp_crs");
            sb.AppendLine("\t\tdirectives `");
            sb.AppendLine("\t\t\tInclude @coraza.conf-recommended");
            sb.AppendLine($"\t\t\tInclude {corazaPath}");
            sb.AppendLine("\t\t`");
            sb.AppendLine("\t}");
            usedErrors = customPage;
        }

        var allow = s.AllowIps.Count > 0 ? string.Join(' ', WebSpaceStore.SanitizeDenyIps(s.AllowIps)) : "";
        void Matcher(string name, params string[] lines)
        {
            sb.AppendLine($"\t@{name} {{");
            foreach (var line in lines)
                sb.AppendLine($"\t\t{line}");
            if (allow.Length > 0)
                sb.AppendLine($"\t\tnot remote_ip {allow}");
            sb.AppendLine("\t}");
        }

        // Rate limit and login limit (Block mode, needs the rate-limit module). Per visitor address; allow-listed IPs are exempt.
        // The module has no ban time: a visitor over the limit gets 429 until the window frees up again.
        var zones = new List<string>();
        if (block && CorazaProbe.RateLimitAvailable())
        {
            var id = space.Uuid.ToString("N"); // unique per WebSpace: zone names are global to the handler config
            var allowMatch = allow.Length > 0 ? new[] { $"\t\t\t\tnot remote_ip {allow}" } : Array.Empty<string>();
            if (s.RateLimit.Enabled)
            {
                // Page requests only: one page view pulls in dozens of images, scripts and fonts, which would use the limit up on a reload.
                var z = new List<string>
                {
                    $"\t\tzone fq{id}_all {{",
                    "\t\t\tmatch {",
                    $"\t\t\t\tnot path {string.Join(' ', AssetPatterns)}",
                };
                z.AddRange(allowMatch);
                z.Add("\t\t\t}");
                z.Add("\t\t\tkey {http.request.remote.host}");
                z.Add("\t\t\twindow 1m");
                z.Add($"\t\t\tevents {s.RateLimit.PerMinute}");
                z.Add("\t\t}");
                zones.AddRange(z);
            }

            var loginPaths = PathPatterns(s.Login.Paths.Where(WafSettingsSanitizer.IsValidPath));
            if (s.Login.Enabled && loginPaths.Count > 0)
            {
                var z = new List<string> { $"\t\tzone fq{id}_login {{", "\t\t\tmatch {", $"\t\t\t\tpath {string.Join(' ', loginPaths)}" };
                if (allowMatch.Length > 0)
                    z.Add(allowMatch[0][0..]);
                z.Add("\t\t\t}");
                z.Add("\t\t\tkey {http.request.remote.host}");
                z.Add("\t\t\twindow 1m");
                z.Add($"\t\t\tevents {s.Login.PerMinute}");
                z.Add("\t\t}");
                zones.AddRange(z);
            }
        }

        if (zones.Count > 0)
        {
            sb.AppendLine("\trate_limit {");
            foreach (var line in zones)
                sb.AppendLine(line);
            sb.AppendLine("\t}");
            usedErrors = customPage;
            limited = true;
        }

        // Response headers
        var h = s.Headers;
        var headers = new List<string>();
        if (h.Hsts && space.Ssl)
            headers.Add("Strict-Transport-Security \"max-age=31536000;\"");
        if (h.Nosniff)
            headers.Add("X-Content-Type-Options nosniff");
        if (h.Frame == "sameorigin")
            headers.Add("X-Frame-Options SAMEORIGIN");
        else if (h.Frame == "deny")
            headers.Add("X-Frame-Options DENY");
        if (h.Referrer)
            headers.Add("Referrer-Policy strict-origin-when-cross-origin");
        if (h.Permissions)
            headers.Add("Permissions-Policy \"camera=(), microphone=(), geolocation=()\"");
        if (headers.Count > 0)
        {
            sb.AppendLine("\theader {");
            foreach (var line in headers)
                sb.AppendLine($"\t\t{line}");
            sb.AppendLine("\t}");
        }

        sb.AppendLine("\trequest_body {");
        sb.AppendLine($"\t\tmax_size {s.Http.MaxBodyMb}MB");
        sb.AppendLine("\t}");

        var rules = new List<string>();

        // Request line limits
        Matcher("fq_longurl", $"expression `size({{http.request.uri}}) > {s.Http.MaxUrlLength}`");
        rules.Add(Deny("@fq_longurl", 414));

        // Caddy 2.6 answers an oversized proxied upload with 502; refuse declared sizes up front with a clean 413.
        // (Caddy counts 1 MB = 1,000,000 bytes, same as the request_body limit below.)
        Matcher("fq_bigbody",
            $"expression `size({{http.request.header.Content-Length}}) > 0 && int({{http.request.header.Content-Length}}) > {s.Http.MaxBodyMb * 1_000_000L}`");
        rules.Add(Deny("@fq_bigbody", 413));

        if (s.Http.Methods.Count < 7)
        {
            Matcher("fq_method", $"not method {string.Join(' ', s.Http.Methods)}");
            rules.Add(Deny("@fq_method", 405));
        }

        // Customer lists
        var denyIps = WebSpaceStore.SanitizeDenyIps(space.WafDenyIps);
        if (denyIps.Count > 0)
        {
            Matcher("fq_denyip", $"remote_ip {string.Join(' ', denyIps)}");
            rules.Add(Deny("@fq_denyip", status));
        }

        var denyPaths = PathPatterns(space.WafDenyPaths.Where(WafSettingsSanitizer.IsValidPath));
        if (denyPaths.Count > 0)
        {
            Matcher("fq_denypath", $"path {string.Join(' ', denyPaths)}");
            rules.Add(Deny("@fq_denypath", status));
        }

        // Hotlink protection: only requests that carry a foreign Referer are refused (no Referer = direct access = allowed).
        var hot = s.Hotlink;
        if (hot.Enabled && hot.Extensions.Count > 0)
        {
            var hosts = domains.Select(d => d.Trim().ToLowerInvariant())
                .Where(d => WafSettingsSanitizer.IsSafeToken(d, 253))
                .Concat(hot.AllowedHosts)
                .Distinct()
                .Select(HostRegex)
                .ToList();
            if (hosts.Count > 0)
            {
                Matcher("fq_hotlink",
                    $"path_regexp fq_hot (?i)\\.({string.Join('|', hot.Extensions)})$",
                    "header Referer *",
                    $"not header_regexp Referer ^https?://({string.Join('|', hosts)})(:[0-9]+)?(/|$)");
                rules.Add(Deny("@fq_hotlink", status));
            }
        }

        // Country filter (Block mode only): address ranges come from files, see WafGeoData.
        if (block)
        {
            var geoFile = WafGeoData.PathFor(rootDirectory, s.Geo);
            if (geoFile is not null)
            {
                Matcher("fq_geo", $"import {geoFile}");
                rules.Add(Deny("@fq_geo", status));
            }
        }

        // Anti-bot (Block mode only)
        if (block)
        {
            var ab = s.AntiBot;
            if (ab.ScannerPaths)
            {
                Matcher("fq_scanner", $"path {string.Join(' ', ScannerPaths)}");
                rules.Add(Deny("@fq_scanner", status));
            }

            if (ab.BadAgents)
            {
                Matcher("fq_badagent", $"header_regexp User-Agent {BadAgentPattern}");
                rules.Add(Deny("@fq_badagent", status));
            }

            if (ab.EmptyAgent)
            {
                // The MTA-STS policy is fetched by mail servers, many of which send no User-Agent at all
                // (RFC 8461 does not require one). A refusal reads as "no policy", so this one public file is exempt.
                Matcher("fq_noagent", "not header User-Agent *", "not path /.well-known/mta-sts.txt");
                rules.Add(Deny("@fq_noagent", status));
                // "header User-Agent *" counts a present-but-blank header as set, so a blank one needs its own matcher.
                Matcher("fq_blankagent", "header_regexp User-Agent ^\\s*$", "not path /.well-known/mta-sts.txt");
                rules.Add(Deny("@fq_blankagent", status));
            }

            var agents = ab.CustomAgents.Where(a => AgentSafe(a)).Select(Regex.Escape).ToList();
            if (agents.Count > 0)
            {
                Matcher("fq_customagent", $"header_regexp User-Agent (?i)({string.Join('|', agents)})");
                rules.Add(Deny("@fq_customagent", status));
            }
        }

        foreach (var rule in rules)
            sb.AppendLine(rule);

        if (usedErrors)
        {
            sb.AppendLine("\thandle_errors {");
            // "interruption triggered" is how Coraza reports a request it stopped.
            var messages = corazaPath is not null ? $"in [\"{ErrorMessage}\", \"interruption triggered\"]" : $"== \"{ErrorMessage}\"";
            var expression = $"{{http.error.message}} {messages}";
            if (limited)
                expression += " || {http.error.status_code} == 429";
            sb.AppendLine($"\t\t@fq_page expression `{expression}`");
            sb.AppendLine("\t\thandle @fq_page {");
            sb.AppendLine($"\t\t\troot * {pageDir}");
            sb.AppendLine($"\t\t\trewrite * /{WafBlockPageFiles.FileName}");
            sb.AppendLine("\t\t\theader Cache-Control \"no-store\"");
            sb.AppendLine("\t\t\ttemplates");
            sb.AppendLine("\t\t\tfile_server");
            sb.AppendLine("\t\t}");
            sb.AppendLine("\t}");
        }
    }

    /// <summary>True when the generated Caddyfile needs the global <c>order rate_limit before basic_auth</c> option for this WebSpace.</summary>
    internal static bool UsesRateLimit(WebSpace space)
    {
        if (!space.WafEnabled || space.WafSettings is null || !CorazaProbe.RateLimitAvailable())
            return false;
        var s = WafSettingsSanitizer.Sanitize(space.WafSettings);
        return s.Mode == "block" && (s.RateLimit.Enabled || (s.Login.Enabled && s.Login.Paths.Count > 0));
    }

    /// <summary>True when the generated Caddyfile needs the global <c>order coraza_waf first</c> option for this WebSpace.</summary>
    internal static bool UsesCoraza(WebSpace space) => CorazaProbe.IsAvailable() && CorazaRules.IsActive(space);

    /// <summary>Each denied path blocks itself and everything below it.</summary>
    internal static List<string> PathPatterns(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var raw in paths)
        {
            var p = raw.TrimEnd('/');
            if (p.Length == 0 || p.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase))
                continue;
            if (p.EndsWith('*'))
            {
                result.Add(p);
                continue;
            }

            result.Add(p);
            result.Add(p + "/*");
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string HostRegex(string host)
    {
        if (host.StartsWith("*.", StringComparison.Ordinal))
            return "([a-z0-9-]+\\.)+" + Regex.Escape(host[2..]);
        return Regex.Escape(host);
    }

    private static bool AgentSafe(string agent)
    {
        if (agent.Length is < 2 or > 80)
            return false;
        // Letters, digits and a few separators only: nothing that is special to the Caddyfile after regex escaping.
        return agent.All(c => char.IsLetterOrDigit(c) && c < 128 || c is '.' or '/' or '-' or '_' or ':' or ';' or ',' or '(' or ')' or '+' or '=') && !agent.Contains(' ');
    }
}
