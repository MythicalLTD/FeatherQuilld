using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Native;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// Ownership handling for containers whose web server does not run as root.
/// </summary>
public static class WebSpaceFsOwnership
{
    /// <summary>www-data in the official php:*-apache images.</summary>
    public const int WebServerUid = 33;

    /// <summary>www-data group in the official php:*-apache images.</summary>
    public const int WebServerGid = 33;

    /// <summary>Runtimes that serve through Apache/PHP running as www-data.</summary>
    public static bool IsWebServerRuntime(string runtime) =>
        string.Equals(runtime, "php", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uid the quota mount should assign to entries created through it, or null to keep
    /// the configured default. Without this every directory a PHP app creates (uploads,
    /// caches, plugin folders) would be owned by the configured user, so the web server
    /// could create it but not write into it.
    /// </summary>
    public static int? MountOwnerUid(string runtime) =>
        IsWebServerRuntime(runtime) ? WebServerUid : null;

    /// <inheritdoc cref="MountOwnerUid"/>
    public static int? MountOwnerGid(string runtime) =>
        IsWebServerRuntime(runtime) ? WebServerGid : null;

    /// <summary>
    /// Apache/PHP runs as www-data, while WebPlate installers, uploads and the file
    /// service write as root. The fusequota mount is created with <c>--uid 0 --gid 0</c>
    /// and presents every entry as root, and it ignores chown issued from inside the
    /// container, so a <c>chown</c> in the container startup script never reaches the
    /// data. The ownership therefore has to be corrected on the source volume from the
    /// host, otherwise the application has no write access at all (WordPress cannot
    /// create wp-config.php, no uploads, no plugin or core updates).
    /// </summary>
    public static void EnsureWebServerOwnership(
        WebSpace space,
        string fsPath,
        AppLogger? logger = null,
        SystemConfig? system = null)
    {
        if (!IsWebServerRuntime(space.Runtime))
            return;

        var source = ResolveSourceVolume(fsPath) ?? fsPath;
        if (!Directory.Exists(source))
            return;

        var result = FqCtl.ChownRecursive(WebServerUid, WebServerGid, source, system, logger);
        if (result.Succeeded)
        {
            logger?.Debug(LoggerTypes.WebSpaces,
                $"chown {WebServerUid}:{WebServerGid} applied to {source} ({space.Uuid})");
            return;
        }

        var detail = string.IsNullOrEmpty(result.Stderr) ? result.Code.ToString() : result.Stderr;
        logger?.Warning(LoggerTypes.WebSpaces,
            $"chown for {space.Uuid} failed ({result.Code}): {detail}");
    }

    /// <summary>
    /// Maps a mount path back to the volume it was mounted from by looking it up in the
    /// mount table. Returns null when the path is not a mount point (i.e. it already is
    /// the source volume).
    /// </summary>
    private static string? ResolveSourceVolume(string fsPath)
    {
        try
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fsPath));
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length < 2)
                    continue;

                var mountPoint = UnescapeMountField(parts[1]);
                if (!string.Equals(mountPoint, target, StringComparison.Ordinal))
                    continue;

                var sourcePath = UnescapeMountField(parts[0]);
                if (Directory.Exists(sourcePath))
                    return sourcePath;
            }
        }
        catch
        {
            // fall through to the caller-provided path
        }

        return null;
    }

    internal static string UnescapeMountField(string value) =>
        value.Replace("\\040", " ", StringComparison.Ordinal)
             .Replace("\\011", "\t", StringComparison.Ordinal)
             .Replace("\\012", "\n", StringComparison.Ordinal)
             .Replace("\\134", "\\", StringComparison.Ordinal);
}
