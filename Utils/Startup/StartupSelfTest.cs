using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.SystemInfo;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Disk;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.Startup;

/// <summary>Boot-time / on-demand checks: paths, disk limiter, WebSpaces, proxy, panel.</summary>
public static class StartupSelfTest
{
    public static BootStepResult Run(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        BootReporter? reporter = null,
        DiagnosticsRegistry? diagnostics = null)
    {
        var checks = Collect(config, spaces, logger, reporter);
        diagnostics?.SetBootChecks(checks);

        var failures = checks.Count(c => c.Status == "fail");
        var warnings = checks.Count(c => c.Status == "warn");
        var result = new BootStepResult();

        if (failures > 0)
        {
            result.Status = BootStepStatus.Failed;
            logger.Error(LoggerTypes.SelfTest, $"Self-tests finished with {failures} failure(s), {warnings} warning(s)");
        }
        else if (warnings > 0)
        {
            result.Status = BootStepStatus.Warning;
            logger.Warning(LoggerTypes.SelfTest, $"Self-tests finished with {warnings} warning(s)");
        }
        else
        {
            logger.Info(LoggerTypes.SelfTest, "Self-tests passed");
        }

        reporter?.Detail(failures > 0
            ? $"{failures} failed · {warnings} warn"
            : warnings > 0 ? $"{warnings} warning(s)" : "ok");

        return result;
    }

    /// <summary>Re-run checks for the diagnostics API without failing boot.</summary>
    public static IReadOnlyList<DiagnosticCheck> RunLive(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        DiagnosticsRegistry diagnostics)
    {
        var checks = Collect(config, spaces, logger, reporter: null);
        diagnostics.SetLiveChecks(checks);
        return checks;
    }

    private static List<DiagnosticCheck> Collect(
        AppConfig config,
        WebSpaceStore spaces,
        AppLogger logger,
        BootReporter? reporter)
    {
        logger.Info(LoggerTypes.SelfTest, "Running startup self-tests…");
        logger.Debug(LoggerTypes.SelfTest, $"debug={config.Debug} quiet={config.Quiet}");

        var checks = new List<DiagnosticCheck>();

        checks.Add(CheckDirectory("data", config.System.Data, logger, reporter));
        checks.Add(CheckDirectory("vmounts", config.System.VmountDirectory, logger, reporter));
        checks.Add(CheckDirectory("tmp", config.System.TmpDirectory, logger, reporter));
        checks.Add(CheckWritableProbe(config.System.Data, logger, reporter));
        checks.AddRange(CheckDiskLimiter(config, logger, reporter));
        checks.Add(CheckWebSpaces(spaces, config, logger, reporter));
        checks.Add(CheckProxy(config, logger, reporter));
        checks.Add(CheckPanel(config, logger, reporter));

        return checks;
    }

    private static DiagnosticCheck CheckDirectory(
        string label, string path, AppLogger logger, BootReporter? reporter)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (!Directory.Exists(path))
            {
                Fail($"Directory missing: {label} → {path}", logger, reporter);
                return new DiagnosticCheck($"dir.{label}", "fail", $"Directory missing: {label}", path);
            }

            logger.Debug(LoggerTypes.SelfTest, $"dir ok [{label}] {path}");
            return new DiagnosticCheck($"dir.{label}", "ok", $"Directory ok: {label}", path);
        }
        catch (Exception ex)
        {
            Fail($"Cannot access {label} ({path}): {ex.Message}", logger, reporter);
            return new DiagnosticCheck($"dir.{label}", "fail", $"Cannot access {label}", ex.Message);
        }
    }

    private static DiagnosticCheck CheckWritableProbe(
        string dataPath, AppLogger logger, BootReporter? reporter)
    {
        var probe = Path.Combine(dataPath, $".featherquilld-write-probe-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "ok");
            var roundTrip = File.ReadAllText(probe);
            File.Delete(probe);
            if (roundTrip != "ok")
            {
                Warn($"Write probe mismatch under {dataPath}", logger, reporter);
                return new DiagnosticCheck("write_probe", "warn", "Write probe mismatch", dataPath);
            }

            logger.Debug(LoggerTypes.SelfTest, $"write probe ok → {dataPath}");
            return new DiagnosticCheck("write_probe", "ok", "Data directory writable", dataPath);
        }
        catch (Exception ex)
        {
            Fail($"Data directory not writable ({dataPath}): {ex.Message}", logger, reporter);
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* ignore */ }
            return new DiagnosticCheck("write_probe", "fail", "Data directory not writable", ex.Message);
        }
    }

    private static IEnumerable<DiagnosticCheck> CheckDiskLimiter(
        AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        var mode = config.System.EffectiveDiskLimiterMode;
        logger.Info(LoggerTypes.Disk,
            $"Disk limiter: configured={config.System.DiskLimiterMode} effective={mode} quotas.enabled={config.System.Quotas.Enabled}");

        yield return new DiagnosticCheck(
            "disk_limiter",
            "ok",
            $"Disk limiter mode: {mode}",
            $"configured={config.System.DiskLimiterMode}");

        if (FuseQuotaLimiter.TryResolveBinaryPath(config.System, out var binPath))
        {
            logger.Info(LoggerTypes.Disk, $"fusequota binary → {binPath}");
            yield return new DiagnosticCheck("fusequota_binary", "ok", "fusequota binary found", binPath);
        }
        else
        {
            logger.Debug(LoggerTypes.Disk, $"fusequota not found (configured={config.System.FusequotaPath})");
            if (mode == DiskLimiterModeKind.FuseQuota)
            {
                Fail("FuseQuota mode is on but binary was not found.", logger, reporter);
                yield return new DiagnosticCheck("fusequota_binary", "fail", "fusequota binary missing", config.System.FusequotaPath);
            }
            else
            {
                yield return new DiagnosticCheck("fusequota_binary", "ok", "fusequota not required", null);
            }
        }

        if (mode == DiskLimiterModeKind.FuseQuota && OperatingSystem.IsLinux() && !File.Exists("/dev/fuse"))
        {
            Warn("/dev/fuse missing — install fuse3", logger, reporter);
            yield return new DiagnosticCheck("fuse_device", "warn", "/dev/fuse missing — install fuse3", null);
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return new DiagnosticCheck(
                "fuse_device",
                File.Exists("/dev/fuse") ? "ok" : "warn",
                File.Exists("/dev/fuse") ? "/dev/fuse present" : "/dev/fuse missing",
                null);
        }
    }

    private static DiagnosticCheck CheckWebSpaces(
        WebSpaceStore spaces, AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        var list = spaces.List();
        logger.Info(LoggerTypes.WebSpaces, $"{list.Count} WebSpace(s) under {config.System.Data}");

        var missing = 0;
        foreach (var space in list)
        {
            var dataPath = spaces.DataPath(space.Uuid);
            logger.Debug(LoggerTypes.WebSpaces,
                $"webspace {space.Uuid} webplate={space.WebPlateId} runtime={space.Runtime} domains=[{string.Join(",", space.Domains)}]");

            if (!Directory.Exists(dataPath))
            {
                missing++;
                Warn($"WebSpace {space.Uuid} metadata present but data dir missing", logger, reporter);
            }
        }

        if (missing > 0)
            return new DiagnosticCheck("webspaces", "warn", $"{list.Count} WebSpace(s), {missing} missing data dir", null);

        return new DiagnosticCheck("webspaces", "ok", $"{list.Count} WebSpace(s) loaded", null);
    }

    private static DiagnosticCheck CheckProxy(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!config.System.Proxy.Enabled)
        {
            logger.Debug(LoggerTypes.Proxy, "Reverse proxy disabled");
            return new DiagnosticCheck("proxy", "ok", "Reverse proxy disabled", null);
        }

        logger.Info(LoggerTypes.Proxy, $"Proxy enabled provider={config.System.Proxy.Provider}");
        return new DiagnosticCheck("proxy", "ok", $"Reverse proxy enabled ({config.System.Proxy.Provider})", null);
    }

    private static DiagnosticCheck CheckPanel(AppConfig config, AppLogger logger, BootReporter? reporter)
    {
        if (!config.HasPanelCredentials())
        {
            Warn("No panel credentials — WebSpace create (pull from panel) will fail until configured", logger, reporter);
            return new DiagnosticCheck("panel", "warn", "No panel credentials configured", null);
        }

        logger.Debug(LoggerTypes.SelfTest, $"Panel → {config.Remote.Panel}");
        return new DiagnosticCheck("panel", "ok", "Panel credentials present", config.Remote.Panel);
    }

    private static void Fail(string message, AppLogger logger, BootReporter? reporter)
    {
        logger.Error(LoggerTypes.SelfTest, message);
        reporter?.Detail($"FAIL {message}");
    }

    private static void Warn(string message, AppLogger logger, BootReporter? reporter)
    {
        logger.Warning(LoggerTypes.SelfTest, message);
        reporter?.Detail($"WARN {message}");
    }
}
