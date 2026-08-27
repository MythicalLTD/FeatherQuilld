using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Auth;

/// <summary>
/// Validates panel-issued WebSpace console JWTs (HS256, kid = TokenId, secret = Token).
/// </summary>
public sealed class ConsoleJwtValidator
{
    private readonly AppConfig _config;
    private readonly WebSpaceUserAccessService? _access;

    public ConsoleJwtValidator(AppConfig config, WebSpaceUserAccessService? access = null)
    {
        _config = config;
        _access = access;
    }

    public bool TryValidate(string token, Guid expectedSub, out string? error) =>
        TryValidate(token, expectedSub, out error, out _);

    public bool TryValidate(
        string token,
        Guid expectedSub,
        out string? error,
        out IReadOnlyList<string> permissions)
    {
        error = null;
        permissions = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "Missing token.";
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            error = "Malformed JWT.";
            return false;
        }

        byte[] headerBytes;
        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            headerBytes = Base64UrlDecode(parts[0]);
            payloadBytes = Base64UrlDecode(parts[1]);
            signatureBytes = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            error = "Invalid JWT encoding.";
            return false;
        }

        using var headerDoc = JsonDocument.Parse(headerBytes);
        var header = headerDoc.RootElement;
        var alg = header.TryGetProperty("alg", out var algEl) ? algEl.GetString() : null;
        if (!string.Equals(alg, "HS256", StringComparison.Ordinal))
        {
            error = "Unsupported JWT algorithm.";
            return false;
        }

        var kid = header.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;
        if (!string.Equals(kid, _config.TokenId, StringComparison.Ordinal))
        {
            error = "JWT kid mismatch.";
            return false;
        }

        var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
        var key = Encoding.UTF8.GetBytes(_config.Token);
        var expectedSig = HMACSHA256.HashData(key, signingInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, signatureBytes))
        {
            error = "Invalid JWT signature.";
            return false;
        }

        using var payloadDoc = JsonDocument.Parse(payloadBytes);
        var payload = payloadDoc.RootElement;

        long iat = 0;
        if (payload.TryGetProperty("iat", out var iatEl))
        {
            if (iatEl.ValueKind == JsonValueKind.Number)
                iat = iatEl.TryGetInt64(out var i) ? i : (long)iatEl.GetDouble();
            else
                long.TryParse(iatEl.GetString(), out iat);
        }

        if (payload.TryGetProperty("exp", out var expEl))
        {
            long exp;
            if (expEl.ValueKind == JsonValueKind.Number)
                exp = expEl.TryGetInt64(out var e) ? e : (long)expEl.GetDouble();
            else if (!long.TryParse(expEl.GetString(), out exp))
            {
                error = "Invalid JWT exp.";
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (exp < now)
            {
                error = "JWT expired.";
                return false;
            }
        }
        else
        {
            error = "JWT missing exp.";
            return false;
        }

        var sub = payload.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(sub) ||
            !Guid.TryParse(sub, out var subGuid) ||
            subGuid != expectedSub)
        {
            error = "JWT subject mismatch.";
            return false;
        }

        if (_access is not null
            && payload.TryGetProperty("user", out var userEl)
            && Guid.TryParse(userEl.GetString(), out var userUuid)
            && userUuid != Guid.Empty)
        {
            if (_access.IsJwtRevoked(userUuid, expectedSub, iat))
            {
                error = "JWT revoked.";
                return false;
            }

            var live = _access.GetLivePermissions(userUuid, expectedSub);
            if (live is not null)
            {
                permissions = live;
                return true;
            }
        }

        permissions = ParsePermissions(payload);
        return true;
    }

    private static IReadOnlyList<string> ParsePermissions(JsonElement payload)
    {
        if (!payload.TryGetProperty("permissions", out var permsEl))
            return [ConsolePermissions.Wildcard];

        if (permsEl.ValueKind == JsonValueKind.String)
        {
            var s = permsEl.GetString();
            return string.IsNullOrWhiteSpace(s) ? [ConsolePermissions.Wildcard] : [s];
        }

        if (permsEl.ValueKind != JsonValueKind.Array)
            return [ConsolePermissions.Wildcard];

        var list = new List<string>();
        foreach (var el in permsEl.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                    list.Add(v);
            }
        }

        return list.Count == 0 ? [ConsolePermissions.Wildcard] : list;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
