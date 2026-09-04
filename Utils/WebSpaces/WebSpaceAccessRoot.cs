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
        var result = panel.AuthenticateSftpAsync(authMethod, username, password, publicKey)
            .GetAwaiter().GetResult();

        if (result is null || string.IsNullOrWhiteSpace(result.Server))
            return null;

        if (!Guid.TryParse(result.Server, out var uuid) || spaces.Get(uuid) is null)
            return null;

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
