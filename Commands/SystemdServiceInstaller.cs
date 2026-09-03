using System.Diagnostics;
using System.Text;
using FeatherQuilld.Utils;
using FeatherQuilld.Utils.Config;

namespace FeatherQuilld.Commands;

public sealed class ServiceInstallResult
{
    public bool Installed { get; init; }
    public bool Enabled { get; init; }
    public bool Started { get; init; }
    public string? UnitPath { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Installs and enables the <c>featherquilld</c> systemd unit on Linux.
/// </summary>
public static class SystemdServiceInstaller
{
    public const string ServiceName = "featherquilld";
    public const string UnitFileName = "featherquilld.service";
    public const string DefaultUnitPath = "/etc/systemd/system/featherquilld.service";

    public static bool CanInstall() =>
        OperatingSystem.IsLinux() && IsRoot();

    public static ServiceInstallResult Install(Config config)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new ServiceInstallResult
            {
                Message = "Systemd install is only supported on Linux.",
            };
        }

        if (!IsRoot())
        {
            return new ServiceInstallResult
            {
                Message = "Systemd install requires root. Re-run with sudo configure …",
            };
        }

        var executable = ResolveExecutablePath();
        if (executable is null)
        {
            return new ServiceInstallResult
            {
                Message = "Could not locate featherquilld binary. Publish first: dotnet publish -c Release -o /usr/local/lib/featherquilld",
            };
        }

        if (Path.GetFileName(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceInstallResult
            {
                Message = "Cannot install a dotnet-run service. Publish a Release build first.",
            };
        }

        var unitPath = DefaultUnitPath;
        var unit = BuildUnitFile(executable, config.FilePath, config.System.Username);

        Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
        File.WriteAllText(unitPath, unit, Encoding.UTF8);

        RunSystemctl("daemon-reload");
        var enabled = RunSystemctl("enable", ServiceName) == 0;
        var started = RunSystemctl("restart", ServiceName) == 0;

        return new ServiceInstallResult
        {
            Installed = true,
            Enabled = enabled,
            Started = started,
            UnitPath = unitPath,
            Message = started
                ? "Service installed and started."
                : "Service installed — check journalctl -u featherquilld.",
        };
    }

    public static string? ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            if (!Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(processPath);
        }

        foreach (var name in new[] { "featherquilld", "FeatherQuilld" })
        {
            var published = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(published))
                return Path.GetFullPath(published);
        }

        return FindOnPath("featherquilld") ?? FindOnPath("FeatherQuilld");
    }

    private static string BuildUnitFile(string executable, string configPath, string? runAsUser)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=FeatherQuilld Web Node Daemon");
        sb.AppendLine("After=network-online.target");
        sb.AppendLine("Wants=network-online.target");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=simple");

        if (!string.IsNullOrWhiteSpace(runAsUser)
            && !runAsUser.Equals("root", StringComparison.OrdinalIgnoreCase)
            && UserExists(runAsUser))
        {
            sb.AppendLine($"User={runAsUser}");
        }

        sb.AppendLine($"ExecStart={Quote(executable)} --config {Quote(configPath)}");
        sb.AppendLine("Restart=on-failure");
        sb.AppendLine("RestartSec=5");
        sb.AppendLine("Environment=DOTNET_ENVIRONMENT=Production");
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");
        return sb.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    private static bool UserExists(string username) =>
        RunCommand("id", "-u", username) == 0;

    private static bool IsRoot()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "id",
                Arguments = "-u",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            if (process is null)
                return false;

            process.WaitForExit();
            return process.StandardOutput.ReadToEnd().Trim() == "0";
        }
        catch
        {
            return false;
        }
    }

    private static int RunSystemctl(params string[] args)
    {
        try
        {
            return RunCommand("systemctl", args);
        }
        catch (Exception ex)
        {
            ColoredConsole.WriteLine($"&eWarning:&r &7systemctl failed: {ex.Message}&r");
            return 1;
        }
    }

    private static int RunCommand(string fileName, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }
}
