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

    public static bool SmtpReachable(AppConfig? config) =>
        PortOpen(config?.System.Mail.SmtpPort ?? 587);

    public static bool ImapReachable(AppConfig? config) =>
        PortOpen(config?.System.Mail.ImapPort ?? 993);

    public static bool MxPortOpen(AppConfig? config) =>
        PortOpen(config?.System.Mail.SmtpPort ?? 25, "127.0.0.1")
        || PortOpen(25, "127.0.0.1");
}
