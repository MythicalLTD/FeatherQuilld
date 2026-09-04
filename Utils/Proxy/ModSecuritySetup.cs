using System.Text;
using System.Text.RegularExpressions;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Resolves and validates ModSecurity + OWASP CRS paths, and builds nginx rules aggregates.
/// </summary>
public static partial class ModSecuritySetup
{
    public const string MainConfPath = "/etc/nginx/modsec/main.conf";
    public const string NginxModSecurityConfPath = "/etc/nginx/modsecurity.conf";

    public static readonly string[] ModSecurityConfCandidates =
    [
        "/etc/modsecurity/modsecurity.conf",
    ];

    public static readonly string[] RecommendedConfCandidates =
    [
        "/etc/modsecurity/modsecurity.conf-recommended",
        "/usr/share/modsecurity-crs/modsecurity.conf-recommended",
        "/usr/share/doc/libmodsecurity3/examples/modsecurity.conf-recommended",
        "/usr/share/doc/modsecurity-crs/examples/modsecurity.conf-recommended",
    ];

    public static readonly string[] CrsSetupCandidates =
    [
        "/usr/share/modsecurity-crs/crs-setup.conf",
        "/usr/share/modsecurity-crs/owasp-crs/crs-setup.conf",
        "/etc/modsecurity/crs/crs-setup.conf",
        "/etc/nginx/modsec/crs-setup.conf",
    ];

    /// <summary>
    /// Ensures a usable modsecurity.conf exists (copying from *-recommended when needed)
    /// and returns CRS setup + rules include paths. Fails if any required piece is missing.
    /// </summary>
    public static bool TryPrepare(
        out string modSecurityConf,
        out string crsSetup,
        out string rulesInclude,
        out string error)
    {
        modSecurityConf = "";
        crsSetup = "";
        rulesInclude = "";
        error = "";

        if (!TryEnsureModSecurityConf(out modSecurityConf, out var confError))
        {
            error = confError;
            return false;
        }

        if (!TryResolveCrs(out crsSetup, out rulesInclude, out var crsError))
        {
            error = crsError;
            return false;
        }

        return true;
    }

    public static bool TryEnsureModSecurityConf(out string path, out string error)
    {
        path = "";
        error = "";

        foreach (var candidate in ModSecurityConfCandidates)
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        const string preferred = "/etc/modsecurity/modsecurity.conf";
        foreach (var recommended in RecommendedConfCandidates)
        {
            if (!File.Exists(recommended))
                continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
                File.Copy(recommended, preferred, overwrite: false);
                path = preferred;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to copy {recommended} → {preferred}: {ex.Message}";
                return false;
            }
        }

        error = "modsecurity.conf not found (and no modsecurity.conf-recommended to copy)";
        return false;
    }

    public static bool TryResolveCrs(out string crsSetup, out string rulesInclude, out string error)
    {
        crsSetup = "";
        rulesInclude = "";
        error = "";

        foreach (var setup in CrsSetupCandidates)
        {
            if (!File.Exists(setup))
                continue;

            var rulesDir = Path.Combine(Path.GetDirectoryName(setup)!, "rules");
            if (!Directory.Exists(rulesDir))
                continue;

            var hasRules = Directory.EnumerateFiles(rulesDir, "*.conf").Any();
            if (!hasRules)
                continue;

            crsSetup = setup;
            rulesInclude = Path.Combine(rulesDir, "*.conf");
            return true;
        }

        error = "OWASP CRS setup/rules not found under known paths";
        return false;
    }

    public static string BuildMainConf(string modSecurityConf, string crsSetup, string rulesInclude)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Include {modSecurityConf}");
        sb.AppendLine($"Include {crsSetup}");
        sb.AppendLine($"Include {rulesInclude}");
        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="path"/> exists and every Include target is resolvable
    /// (files exist; globs match at least one file).
    /// </summary>
    public static bool IsValidRulesFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            var hasInclude = false;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var match = IncludeLineRegex().Match(trimmed);
                if (!match.Success)
                    continue;

                hasInclude = true;
                var includePath = match.Groups[1].Value.Trim().Trim('"', '\'');
                if (!IncludeTargetExists(includePath))
                    return false;
            }

            // Aggregate rules files must Include something; a bare empty file is invalid.
            return hasInclude;
        }
        catch
        {
            return false;
        }
    }

    private static bool IncludeTargetExists(string includePath)
    {
        if (includePath.Contains('*', StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(includePath);
            var pattern = Path.GetFileName(includePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            return Directory.EnumerateFiles(dir, pattern).Any();
        }

        return File.Exists(includePath);
    }

    [GeneratedRegex(@"^Include\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IncludeLineRegex();
}
