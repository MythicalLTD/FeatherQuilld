using System.Diagnostics;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

public static class WebmailProbe
{
    public static bool ContainerRunning(AppConfig? config = null)
    {
        if (!MailProbe.DockerOnPath())
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "ps", "--filter", $"name=^{WebmailPaths.ContainerName}$", "--format", "{{.Names}}" },
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
            return proc.ExitCode == 0 && output.Contains(WebmailPaths.ContainerName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool HttpReachable(AppConfig? config = null) =>
        MailProbe.PortOpen(WebmailPaths.DefaultPort);
}
