using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Logger;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces.Disk;

/// <summary>
/// Downloads the calagopus/fusequota release binary when disk limiting needs it
/// and no local copy exists (Wings-style single-binary deploy).
/// </summary>
public static class FuseQuotaBinaryProvisioner
{
    public const string DefaultReleaseBase =
        "https://github.com/calagopus/fusequota/releases/latest/download";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    static FuseQuotaBinaryProvisioner()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("FeatherQuilld", Startup.StartupBanner.Version));
    }

    /// <summary>Cached binary next to the FeatherQuilld executable.</summary>
    public static string CachePath => Path.Combine(AppContext.BaseDirectory, "fusequota");

    public static bool ShouldAutoProvision(SystemConfig system) =>
        system.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota;

    public static string? ResolveLinuxAssetName()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "fusequota-x86_64-linux",
            Architecture.Arm64 => "fusequota-aarch64-linux",
            Architecture.Ppc64le => "fusequota-ppc64le-linux",
            Architecture.RiscV64 => "fusequota-riscv64-linux",
            _ => null,
        };
    }

    public static string BuildDownloadUrl(string assetName, string? releaseBase = null)
    {
        var root = (releaseBase ?? Environment.GetEnvironmentVariable("FUSEQUOTA_RELEASE_BASE")
                    ?? DefaultReleaseBase).TrimEnd('/');
        return $"{root}/{assetName}";
    }

    /// <summary>
    /// Returns a usable fusequota path, downloading from GitHub releases when missing.
    /// </summary>
    public static async Task<string?> EnsureAsync(
        SystemConfig system,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (FuseQuotaLimiter.TryResolveBinaryPath(system, out var existing))
            return existing;

        if (!ShouldAutoProvision(system))
            return null;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FuseQuotaLimiter.TryResolveBinaryPath(system, out existing))
                return existing;

            return await DownloadToCacheAsync(logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string? Ensure(SystemConfig system, AppLogger? logger = null) =>
        EnsureAsync(system, logger).GetAwaiter().GetResult();

    internal static async Task<string?> DownloadToCacheAsync(
        AppLogger? logger,
        CancellationToken cancellationToken = default)
    {
        var asset = ResolveLinuxAssetName();
        if (asset is null)
        {
            logger?.Warning(
                LoggerTypes.Disk,
                $"fusequota auto-download is not supported on {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
            return null;
        }

        var url = BuildDownloadUrl(asset);
        var dest = CachePath;
        var temp = dest + ".download";

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        logger?.Info(LoggerTypes.Disk, $"Downloading fusequota ({asset}) from GitHub releases…");

        try
        {
            if (File.Exists(temp))
                File.Delete(temp);

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var local = File.Create(temp);
                await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(dest))
                File.Delete(dest);

            File.Move(temp, dest);
            TryMarkExecutable(dest);

            logger?.Info(LoggerTypes.Disk, $"fusequota installed → {dest}");
            return dest;
        }
        catch (Exception ex)
        {
            logger?.Error(LoggerTypes.Disk, $"fusequota download failed ({url}): {ex.Message}");
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // best-effort
            }

            return null;
        }
    }

    private static void TryMarkExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // chmod best-effort
        }
    }
}
