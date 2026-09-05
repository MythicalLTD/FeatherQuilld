using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
        var token = ExtractToken();
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.Fail("Missing Authorization header or token query."));

        // Constant-time comparison to avoid a timing side-channel on the
        // node-wide bearer token, which grants full API access to this daemon.
        if (!FixedTimeStringEquals(token, _config.BearerToken))
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, _config.TokenId)], Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ExtractToken()
    {
        if (Request.Headers.TryGetValue("Authorization", out var headerValues))
        {
            var header = headerValues.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return header["Bearer ".Length..].Trim();
        }

        // WebSocket clients often pass the bearer via ?token=
        if (Request.Query.TryGetValue("token", out var queryToken))
        {
            var value = queryToken.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Constant-time string comparison (UTF-8 byte-wise) to prevent timing
    /// side-channels when comparing against a secret token.
    /// </summary>
    private static bool FixedTimeStringEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        // FixedTimeEquals itself short-circuits on length mismatch only after
        // comparing lengths (not attacker-observable content), which is
        // standard practice and not a meaningful side-channel here.
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
