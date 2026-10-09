using System.Diagnostics;
using System.Net.Sockets;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>Detects docker-mailserver container health on the host.</summary>
public static class MailProbe
{
    public static bool IsAvailable(AppConfig? config = null)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (!DockerOnPath())
            return false;
        return ContainerRunning(config) && SmtpReachable(config) && ImapReachable(config);
    }

    public static bool ContainerRunning(AppConfig? config = null)
    {
        if (!DockerOnPath())
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "ps", "--filter", $"name=^{MailPaths.ContainerName}$", "--format", "{{.Names}}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            return proc.ExitCode == 0 && output.Contains(MailPaths.ContainerName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool DockerOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "version", "--format", "{{.Server.Version}}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool PortOpen(int port, string host = "127.0.0.1")
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(2000))
                return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Host the mail ports are checked on.
    ///
    /// A mail stack on its own host (remote mailcow) is checked there; probing loopback instead
    /// reports "not listening" while the mail server is perfectly up.
    /// </summary>
    public static string ProbeHost(AppConfig? config)
    {
        if (config is null)
            return "127.0.0.1";

        var host = (MailcowDocker.RemoteHost(config) ?? string.Empty).Trim();
        if (host.Length == 0)
            host = MailDnsHelper.ResolveConfiguredMailHost(config);

        return host.Length > 0 ? host : "127.0.0.1";
    }

    public static bool SmtpReachable(AppConfig? config)
    {
        var port = config?.System.Mail.SmtpPort ?? 587;

        return PortOpen(port, ProbeHost(config)) || PortOpen(port);
    }

    public static bool ImapReachable(AppConfig? config)
    {
        var port = config?.System.Mail.ImapPort ?? 993;

        return PortOpen(port, ProbeHost(config)) || PortOpen(port);
    }

    /// <summary>Port 25 (or the submission port) on the mail host.</summary>
    public static bool MxPortOpen(AppConfig? config)
    {
        var host = ProbeHost(config);

        return PortOpen(25, host) || PortOpen(25) || PortOpen(config?.System.Mail.SmtpPort ?? 25, host);
    }

    /// <summary>
    /// True when the mail stack selected in config is running: mailcow resolves its
    /// containers through compose labels, docker-mailserver through its container name.
    /// </summary>
    public static bool StackRunning(AppConfig? config)
    {
        if (config is null)
            return ContainerRunning(null);

        return MailBackendKind.IsMailcow(config.System.Mail.Backend)
            ? MailcowDocker.StackReachable(config)
            : ContainerRunning(config);
    }

    /// <summary>Container/project identifier of the configured stack, for diagnostics.</summary>
    public static string StackIdentifier(AppConfig? config)
    {
        if (config is not null && MailBackendKind.IsMailcow(config.System.Mail.Backend))
        {
            var dovecot = MailcowDocker.FindContainer("dovecot-mailcow");
            if (dovecot is not null)
                return dovecot;

            // A remote stack has no container here; the host name is what diagnostics need.
            var remote = MailcowDocker.RemoteHost(config);

            return remote is not null ? $"{remote} (remote)" : MailcowPaths.ProjectName;
        }

        return MailPaths.ContainerName;
    }

    /// <summary>Compose file of the configured stack, for diagnostics.</summary>
    public static string ComposePath(AppConfig config) =>
        MailBackendKind.IsMailcow(config.System.Mail.Backend)
            ? MailcowPaths.ComposeFile(config)
            : MailPaths.ComposeFile(config);
}
