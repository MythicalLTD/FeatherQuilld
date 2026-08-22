using System.Security.Claims;
using System.Text.Encodings.Web;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FeatherQuilld.Middleware;

/// <summary>
/// Panel → node auth: validates <c>Authorization: Bearer {token_id}.{token}</c>
/// against local config credentials.
/// </summary>
public sealed class BearerTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AppConfig _config;

    public BearerTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppConfig config)
        : base(options, logger, encoder)
    {
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return Task.FromResult(AuthenticateResult.Fail("Missing Authorization header."));

        var header = headerValues.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.Fail("Authorization header must use Bearer scheme."));

        var token = header["Bearer ".Length..].Trim();
        if (!string.Equals(token, _config.BearerToken, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, _config.TokenId)], Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
