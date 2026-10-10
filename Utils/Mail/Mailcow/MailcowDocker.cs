using System.Diagnostics;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>
/// Runs commands inside mailcow containers. mailcow spreads its stack over many
/// containers, so the target container is resolved by compose labels instead of
/// a fixed name (compose v2 suffixes names with a replica index).
/// </summary>
internal static class MailcowDocker
{
    public static string? FindContainer(string service, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("ps");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"label=com.docker.compose.project={MailcowPaths.ProjectName}");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"label=com.docker.compose.service={service}");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("{{.Names}}");

        using var proc = Process.Start(psi);
        if (proc is null)
            return null;
        if (!proc.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return null;
        }

        var output = proc.StandardOutput.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return output.Length > 0 ? output[0] : null;
    }

    /// <summary>
    /// <c>docker exec -i &lt;container&gt; &lt;argv…&gt;</c> with optional stdin. Throws with
    /// the combined output when the command fails, mirroring the docker-mailserver
    /// backend so callers see the tool's own error text.
    /// </summary>
    public static string Exec(string container, IReadOnlyList<string> argv, string? stdin = null,
        int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        if (stdin is not null)
            psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(container);
        foreach (var arg in argv)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker.");

        if (stdin is not null)
        {
            proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new InvalidOperationException("docker exec timed out.");
        }

        if (proc.ExitCode != 0)
        {
            var combined = (stdout + "\n" + stderr).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(combined)
                ? $"docker exec exited with code {proc.ExitCode}"
                : combined);
        }

        return stdout.Trim();
    }

    public static bool StackRunning(AppConfig config, int timeoutMs = 5000) =>
        FindContainer("nginx-mailcow", TimeSpan.FromMilliseconds(timeoutMs)) is not null
        && FindContainer("dovecot-mailcow", TimeSpan.FromMilliseconds(timeoutMs)) is not null;

    /// <summary>
    /// True when the mailcow stack is usable: either the containers run on this host, or the
    /// stack lives on a dedicated mail host and answers on <c>system.mail.mailcow.url</c>
    /// with the configured API key. The panel then manages that remote stack through its API.
    /// </summary>
    public static bool StackReachable(AppConfig config, int timeoutMs = 5000) =>
        StackRunning(config, timeoutMs) || RemoteStackReachable(config);

    public static bool RemoteStackReachable(AppConfig config)
    {
        var mailcow = config.System.Mail.Mailcow;
        if (string.IsNullOrWhiteSpace(mailcow.Url))
            return false;

        if (MailcowApiClient.ResolveApiKey(config).Length == 0)
            return false;

        try
        {
            using var api = new MailcowApiClient(config);
            return api.PingAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Host label for diagnostics: the remote URL when mailcow runs elsewhere.</summary>
    public static string? RemoteHost(AppConfig config)
    {
        var url = (config.System.Mail.Mailcow.Url ?? string.Empty).Trim();
        if (url.Length == 0)
            return null;

        return Uri.TryCreate(url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url, UriKind.Absolute, out var uri)
            ? uri.Host
            : url;
    }
}
