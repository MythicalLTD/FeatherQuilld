using System.Net;
using System.Security.Claims;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.AccountManagement.Directories;
using FubarDev.FtpServer.FileSystem;

namespace FeatherQuilld.Utils.Ftp;

internal sealed class PanelFtpAccountDirectoryQuery : IAccountDirectoryQuery
{
    public IAccountDirectories GetDirectories(IAccountInformation accountInformation)
    {
        var username = accountInformation.FtpUser?.Identity?.Name
            ?? throw new InvalidOperationException("FTP account is missing a username.");

        if (!FtpSessionStore.TryGet(username, out var session) || string.IsNullOrWhiteSpace(session.RootPath))
            throw new InvalidOperationException("FTP session root is not available.");

        var normalized = Path.GetFullPath(session.RootPath);
        return new GenericAccountDirectories(normalized, normalized);
    }
}
