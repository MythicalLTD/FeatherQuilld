using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Sftp;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Resolves panel SFTP auth into a rooted filesystem path for WebSpace file access.</summary>
public static class WebSpaceAccessRoot
{
    public static SftpAuthResult? Resolve(
        IPanelClient panel,
        WebSpaceStore spaces,
        string authMethod,
        string username,
        string password,
        string? publicKey = null,
        AppLogger? logger = null)
    {
        // Per-username brute-force lockout, independent of the panel's own
        // auth latency. Public-key auth attempts are not password guesses,
        // so they don't count against or get blocked by this guard.
        var isPasswordAuth = string.Equals(authMethod, "password", StringComparison.OrdinalIgnoreCase);
        if (isPasswordAuth)
        {
            var remaining = SftpBruteForceGuard.GetLockoutRemaining(username);
            if (remaining > TimeSpan.Zero)
            {
                logger?.Warning(LoggerTypes.Application,
                    $"SFTP/FTP login for user={username} blocked: locked out for {remaining.TotalSeconds:F0}s after repeated failures");
                return null;
            }
        }

        var result = panel.AuthenticateSftpAsync(authMethod, username, password, publicKey)
            .GetAwaiter().GetResult();

        if (result is null || string.IsNullOrWhiteSpace(result.Server))
        {
            if (isPasswordAuth)
                SftpBruteForceGuard.RecordFailure(username);
            return null;
        }

        if (!Guid.TryParse(result.Server, out var uuid) || spaces.Get(uuid) is null)
        {
            if (isPasswordAuth)
                SftpBruteForceGuard.RecordFailure(username);
            return null;
        }

        if (isPasswordAuth)
            SftpBruteForceGuard.Clear(username);

        var basePath = spaces.ResolveAccessFsPath(uuid, logger);
        var relative = WebSpaceStore.NormalizeDocumentRoot(result.RelativeRoot);
        result.RootPath = string.IsNullOrEmpty(relative)
            ? basePath
            : Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(result.RootPath);
        if (string.IsNullOrWhiteSpace(result.User))
            result.User = username;
        return result;
    }
}
