using System.Diagnostics;
using System.Net.Http.Headers;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Dns;

/// <summary>Detects PowerDNS authoritative server + HTTP API on the host.</summary>
public static class PowerDnsProbe
{
    public static bool IsAvailable(AppConfig? config = null)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (ResolveBinary() is null)
            return false;

        var apiKey = config?.System.Dns.PowerDnsApiKey?.Trim() ?? "";
        if (apiKey.Length == 0)
            return File.Exists(ResolveApiKeyPath(config));

        return ApiReachable(config);
    }

    public static string? ResolveBinary()
    {
        foreach (var name in new[] { "pdns_server", "pdns" })
        {
            var path = Which(name);
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }

        return null;
    }

    public static string ResolveApiKeyPath(AppConfig? config = null)
    {
        var root = config?.System.RootDirectory ?? "/var/lib/featherquilld";
        return Path.Combine(root, "dns", "powerdns-api-key");
    }

    public static string ResolveApiKey(AppConfig config)
    {
        var fromConfig = config.System.Dns.PowerDnsApiKey?.Trim() ?? "";
        if (fromConfig.Length > 0)
            return fromConfig;

        var path = ResolveApiKeyPath(config);
        if (File.Exists(path))
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch
            {
                // ignore
            }
        }

        return "";
    }

    public static bool ApiReachable(AppConfig? config = null)
    {
        if (config is null)
            return false;

        var key = ResolveApiKey(config);
        if (key.Length == 0)
            return false;

        try
        {
            using var client = BuildClient(config, key);
            using var response = client.GetAsync("/api/v1/servers/localhost/zones?limit=1")
                .GetAwaiter()
                .GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    internal static HttpClient BuildClient(AppConfig config, string apiKey)
    {
        var baseUrl = (config.System.Dns.PowerDnsApiUrl ?? "http://127.0.0.1:8081").TrimEnd('/') + "/";
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string? Which(string binary)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/which",
                ArgumentList = { binary },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            if (proc.ExitCode != 0)
                return null;

            var path = proc.StandardOutput.ReadToEnd().Trim();
            return path.Length > 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
