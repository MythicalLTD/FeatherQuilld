using System.Diagnostics;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Logger;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Native;

/// <summary>
/// Exit codes from the <c>fqctl</c> C++ helper. Keep in sync with
/// <c>native/fqctl/src/main.cpp</c>.
/// </summary>
public enum FqCtlExitCode
{
    Ok = 0,
    Usage = 1,
    NotFound = 2,
    Permission = 3,
    Busy = 4,
    OsError = 5,
    /// <summary>Binary missing / could not start (C# side only).</summary>
    Unavailable = 100,
}

public readonly record struct FqCtlResult(FqCtlExitCode Code, string Stderr)
{
    public bool Succeeded => Code == FqCtlExitCode.Ok;
}

/// <summary>
/// Resolves and runs the <c>fqctl</c> helper (umount-lazy / chown-r / mount-bind).
/// Falls back to system <c>umount</c> / <c>chown</c> when the binary is absent.
/// </summary>
public static class FqCtl
{
    public const string DefaultBinaryName = "fqctl";

    /// <summary>Cached binary next to the FeatherQuilld executable.</summary>
    public static string CachePath => Path.Combine(AppContext.BaseDirectory, DefaultBinaryName);

    public static string ResolveBinaryPath(SystemConfig? system = null) =>
        TryResolveBinaryPath(system, out var path) ? path : (system?.FqctlPath ?? DefaultBinaryName);

    public static bool TryResolveBinaryPath(SystemConfig? system, out string path)
    {
        var configured = system?.FqctlPath?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            path = Path.GetFullPath(configured);
            return true;
        }

        var env = Environment.GetEnvironmentVariable("FQCTL_BIN");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            path = Path.GetFullPath(env);
            return true;
        }

        if (File.Exists(CachePath))
        {
            path = CachePath;
            return true;
        }

        var bins = Path.Combine(Directory.GetCurrentDirectory(), "bins", DefaultBinaryName);
        if (File.Exists(bins))
        {
            path = Path.GetFullPath(bins);
            return true;
        }

        var nativeBuild = Path.Combine(Directory.GetCurrentDirectory(), "native", "fqctl", "build", DefaultBinaryName);
        if (File.Exists(nativeBuild))
        {
            path = Path.GetFullPath(nativeBuild);
            return true;
        }

        var name = string.IsNullOrWhiteSpace(configured) || configured.Contains(Path.DirectorySeparatorChar)
            ? DefaultBinaryName
            : configured;

        var onPath = FindOnPath(name);
        if (!string.IsNullOrEmpty(onPath))
        {
            path = onPath;
            return true;
        }

        path = string.IsNullOrWhiteSpace(configured) ? DefaultBinaryName : configured;
        return false;
    }

    public static bool IsBinaryAvailable(SystemConfig? system = null) =>
        TryResolveBinaryPath(system, out _);

    public static FqCtlResult UmountLazy(string path, SystemConfig? system = null, AppLogger? logger = null)
    {
        if (TryResolveBinaryPath(system, out var bin))
            return Run(bin, ["umount-lazy", path], logger);

        return RunFallback("umount", ["-l", path], logger, "umount -l");
    }

    public static async Task<FqCtlResult> UmountLazyAsync(
        string path,
        SystemConfig? system = null,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveBinaryPath(system, out var bin))
            return await RunAsync(bin, ["umount-lazy", path], logger, cancellationToken).ConfigureAwait(false);

        return await RunFallbackAsync("umount", ["-l", path], logger, "umount -l", cancellationToken)
            .ConfigureAwait(false);
    }

    public static FqCtlResult ChownRecursive(
        int uid,
        int gid,
        string path,
        SystemConfig? system = null,
        AppLogger? logger = null,
        int timeoutMs = 120_000)
    {
        var owner = $"{uid}:{gid}";
        if (TryResolveBinaryPath(system, out var bin))
            return Run(bin, ["chown-r", owner, path], logger, timeoutMs);

        return RunFallback("chown", ["-R", owner, "--", path], logger, "chown -R", timeoutMs);
    }

    public static FqCtlResult MountBind(
        string source,
        string target,
        SystemConfig? system = null,
        AppLogger? logger = null)
    {
        if (TryResolveBinaryPath(system, out var bin))
            return Run(bin, ["mount-bind", source, target], logger);

        return RunFallback("mount", ["--bind", source, target], logger, "mount --bind");
    }

    public static FqCtlResult Run(
        string binary,
        IReadOnlyList<string> args,
        AppLogger? logger = null,
        int timeoutMs = 60_000)
    {
        try
        {
            var psi = CreateStartInfo(binary, args);
            using var process = Process.Start(psi);
            if (process is null)
            {
                logger?.Warning(LoggerTypes.Disk, $"fqctl could not start ({binary})");
                return new FqCtlResult(FqCtlExitCode.Unavailable, "failed to start");
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new FqCtlResult(FqCtlExitCode.OsError, "timed out");
            }

            var stderr = process.StandardError.ReadToEnd().Trim();
            return new FqCtlResult(MapExitCode(process.ExitCode), stderr);
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Disk, $"fqctl failed: {ex.Message}");
            return new FqCtlResult(FqCtlExitCode.Unavailable, ex.Message);
        }
    }

    public static async Task<FqCtlResult> RunAsync(
        string binary,
        IReadOnlyList<string> args,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = CreateStartInfo(binary, args);
            using var process = Process.Start(psi);
            if (process is null)
            {
                logger?.Warning(LoggerTypes.Disk, $"fqctl could not start ({binary})");
                return new FqCtlResult(FqCtlExitCode.Unavailable, "failed to start");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return new FqCtlResult(MapExitCode(process.ExitCode), stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Disk, $"fqctl failed: {ex.Message}");
            return new FqCtlResult(FqCtlExitCode.Unavailable, ex.Message);
        }
    }

    internal static FqCtlExitCode MapExitCode(int code) => code switch
    {
        0 => FqCtlExitCode.Ok,
        1 => FqCtlExitCode.Usage,
        2 => FqCtlExitCode.NotFound,
        3 => FqCtlExitCode.Permission,
        4 => FqCtlExitCode.Busy,
        5 => FqCtlExitCode.OsError,
        _ => FqCtlExitCode.OsError,
    };

    private static FqCtlResult RunFallback(
        string fileName,
        IReadOnlyList<string> args,
        AppLogger? logger,
        string label,
        int timeoutMs = 60_000)
    {
        try
        {
            var psi = CreateStartInfo(fileName, args);
            using var process = Process.Start(psi);
            if (process is null)
            {
                logger?.Warning(LoggerTypes.Disk, $"could not start {label}");
                return new FqCtlResult(FqCtlExitCode.Unavailable, "failed to start");
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new FqCtlResult(FqCtlExitCode.OsError, "timed out");
            }

            var stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0)
                return new FqCtlResult(FqCtlExitCode.Ok, stderr);

            return new FqCtlResult(FqCtlExitCode.OsError, stderr);
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Disk, $"{label} failed: {ex.Message}");
            return new FqCtlResult(FqCtlExitCode.Unavailable, ex.Message);
        }
    }

    private static async Task<FqCtlResult> RunFallbackAsync(
        string fileName,
        IReadOnlyList<string> args,
        AppLogger? logger,
        string label,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = CreateStartInfo(fileName, args);
            using var process = Process.Start(psi);
            if (process is null)
            {
                logger?.Warning(LoggerTypes.Disk, $"could not start {label}");
                return new FqCtlResult(FqCtlExitCode.Unavailable, "failed to start");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == 0)
                return new FqCtlResult(FqCtlExitCode.Ok, stderr.Trim());

            return new FqCtlResult(FqCtlExitCode.OsError, stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Disk, $"{label} failed: {ex.Message}");
            return new FqCtlResult(FqCtlExitCode.Unavailable, ex.Message);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
