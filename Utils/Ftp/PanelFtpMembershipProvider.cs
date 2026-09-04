using System.Security.Claims;
using FubarDev.FtpServer.AccountManagement;
using FeatherQuilld.Utils.Config.Ftp;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.WebSpaces;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Ftp;

internal sealed class PanelFtpMembershipProvider : IMembershipProvider
{
    private readonly FtpConfig _config;
    private readonly WebSpaceStore _spaces;
    private readonly IPanelClient _panel;
    private readonly AppLogger? _logger;

    public PanelFtpMembershipProvider(
        FtpConfig config,
        WebSpaceStore spaces,
        IPanelClient panel,
        AppLogger? logger = null)
    {
        _config = config;
        _spaces = spaces;
        _panel = panel;
        _logger = logger;
    }

    public Task<MemberValidationResult> ValidateUserAsync(string username, string password)
    {
        if (_config.DisablePasswordAuth)
            return Task.FromResult(new MemberValidationResult(MemberValidationStatus.InvalidLogin));

        var auth = WebSpaceAccessRoot.Resolve(_panel, _spaces, "password", username, password, logger: _logger);
        if (auth is null || string.IsNullOrWhiteSpace(auth.RootPath))
        {
            _logger?.Debug(LoggerTypes.Application, $"FTP auth failed for user={username}");
            return Task.FromResult(new MemberValidationResult(MemberValidationStatus.InvalidLogin));
        }

        var claims = new List<Claim>
        {
            new(ClaimsIdentity.DefaultNameClaimType, username),
            new(FtpAuthClaims.RootPath, auth.RootPath),
            new(FtpAuthClaims.ReadOnly, auth.IsReadOnly ? "1" : "0"),
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "panel"));
        FtpSessionStore.Set(username, new FtpSessionContext(auth.RootPath, auth.IsReadOnly));
        _logger?.Info(LoggerTypes.Application, $"FTP auth ok user={username} webspace={auth.Server}");
        return Task.FromResult(new MemberValidationResult(MemberValidationStatus.AuthenticatedUser, principal));
    }

    public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var username = principal.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(username))
            FtpSessionStore.Remove(username);
        return Task.CompletedTask;
    }
}
