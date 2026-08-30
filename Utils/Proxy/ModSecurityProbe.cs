using System.Diagnostics;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>Detects host ModSecurity + OWASP CRS for nginx WAF emission.</summary>
public static class ModSecurityProbe
{
    private static readonly string[] RulesCandidates =
    [
        "/etc/nginx/modsec/main.conf",
        "/etc/nginx/modsecurity.conf",
        "/etc/modsecurity/modsecurity.conf",
        "/usr/share/modsecurity-crs/owsap-crs/crs-setup.conf",
        "/usr/share/modsecurity-crs/crs-setup.conf",
        "/etc/modsecurity/crs/crs-setup.conf",
    ];

    private static readonly string[] ModuleHintPaths =
    [
        "/etc/nginx/modules-enabled/50-mod-http-modsecurity.conf",
        "/usr/share/nginx/modules/ngx_http_modsecurity_module.so",
        "/usr/lib/nginx/modules/ngx_http_modsecurity_module.so",
    ];

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (ResolveRulesFile() is null)
            return false;
        return ModuleLikelyPresent();
    }

    public static string? ResolveRulesFile()
    {
        foreach (var path in RulesCandidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static bool ModuleLikelyPresent()
    {
        foreach (var path in ModuleHintPaths)
        {
            if (File.Exists(path))
                return true;
        }

        // nginx -V often lists --with-compat / --add-dynamic-module=...modsecurity
        var binary = ProxyProbe.ResolveBinary("nginx");
        if (string.IsNullOrWhiteSpace(binary))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binary,
                ArgumentList = { "-V" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }

            var combined = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            return combined.Contains("modsecurity", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
