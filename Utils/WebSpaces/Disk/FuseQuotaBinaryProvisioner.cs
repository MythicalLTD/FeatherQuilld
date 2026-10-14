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
    /// <summary>
    /// Pinned fusequota release tag. Files are downloaded as root and executed, so
    /// the build is pinned instead of tracking the moving <c>releases/latest</c>.
    /// </summary>
    public const string DefaultReleaseTag = "a39bc56";

    public const string DefaultReleaseBase =
        "https://github.com/calagopus/fusequota/releases/download/" + DefaultReleaseTag;

    /// <summary>
    /// SHA-256 of the pinned release assets (computed from the published binaries of
    /// tag <see cref="DefaultReleaseTag"/>). A mismatch aborts the install. When the
    /// operator points <c>FUSEQUOTA_RELEASE_BASE</c> at their own build, no hash is
    /// known and verification is skipped.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PinnedSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fusequota-x86_64-linux"] = "b339e7d89fccc97c85e8575df61b55564f77091da822b421f23f07631d189992",
            ["fusequota-aarch64-linux"] = "e5a8702c91c43e10377bfaadb856f0d8c8e6d7eb4e8f54a0fbde7a3066f8554a",
            ["fusequota-ppc64le-linux"] = "6fc60ccaf86d143486a56343dd09c4d7a739a85606ad4d9cb2de5503cf89001a",
            ["fusequota-riscv64-linux"] = "574c6fbfbda83ee04cd4b870d3dd0e405d185c7f68524c0a81edfeb287b020b2",
        };

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

    /// <summary>Release base actually in effect (env override or the pinned default).</summary>
    internal static string ResolveReleaseBase() =>
        (Environment.GetEnvironmentVariable("FUSEQUOTA_RELEASE_BASE") ?? DefaultReleaseBase).TrimEnd('/');

    /// <summary>
    /// Expected SHA-256 for an asset, or null when the download source is not the
    /// pinned release (then nothing is known to compare against).
    /// </summary>
    internal static string? ExpectedSha256(string assetName, string? releaseBase = null)
    {
        var custom = releaseBase ?? Environment.GetEnvironmentVariable("FUSEQUOTA_RELEASE_BASE");
        if (!string.IsNullOrWhiteSpace(custom) &&
            !string.Equals(custom.TrimEnd('/'), DefaultReleaseBase, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return PinnedSha256.TryGetValue(assetName, out var hash) ? hash : null;
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
        var expectedHash = ExpectedSha256(asset, ResolveReleaseBase());

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

            if (expectedHash is not null)
            {
                var actualHash = await ComputeSha256Async(temp, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.Error(
                        LoggerTypes.Disk,
                        $"fusequota checksum mismatch for {asset} ({url}): expected {expectedHash}, got {actualHash} — refusing to install.");
                    TryDeleteFile(temp);
                    return null;
                }
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256
            .HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort
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
