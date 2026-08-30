using System.Diagnostics;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>Best-effort detection of reverse-proxy CLI binaries on the host PATH.</summary>
public static class ProxyProbe
{
    public static string NormalizeProvider(string? provider) =>
        (provider ?? "caddy").Trim().ToLowerInvariant() switch
        {
            "nginx" => "nginx",
            "traefik" => "traefik",
            _ => "caddy",
        };

    public static string BinaryName(string provider) => NormalizeProvider(provider) switch
    {
        "nginx" => "nginx",
        "traefik" => "traefik",
        _ => "caddy",
    };

    public static bool BinaryOnPath(string provider) => !string.IsNullOrWhiteSpace(ResolveBinary(provider));

    public static string? ResolveBinary(string provider)
    {
        var name = BinaryName(provider);
        if (OperatingSystem.IsWindows())
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), name + ".exe");
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", $"command -v {name}" },
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

            var resolved = proc.StandardOutput.ReadToEnd().Trim();
            return resolved.Length > 0 ? resolved : null;
        }
        catch
        {
            return null;
        }
    }
}
