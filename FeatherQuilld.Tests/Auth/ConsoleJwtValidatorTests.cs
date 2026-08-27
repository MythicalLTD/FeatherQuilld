using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherQuilld.Utils.Auth;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Auth;

public class ConsoleJwtValidatorTests
{
    private static readonly Guid SpaceId = Guid.Parse("f32612b4-d1ef-4882-af2c-5818d2e885b4");
    private const string TokenId = "test-token-id";
    private const string TokenSecret = "test-token-secret-with-enough-bytes";

    private readonly ConsoleJwtValidator _validator = new(new AppConfig
    {
        TokenId = TokenId,
        Token = TokenSecret,
    });

    [Fact]
    public void TryValidate_ValidToken_Succeeds()
    {
        var jwt = MintJwt(SpaceId, TokenId, TokenSecret, expOffsetSeconds: 3600);
        Assert.True(_validator.TryValidate(jwt, SpaceId, out var error), error);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_Empty_Fails()
    {
        Assert.False(_validator.TryValidate("", SpaceId, out var error));
        Assert.Contains("Missing", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_Malformed_Fails()
    {
        Assert.False(_validator.TryValidate("not.a.jwt.really", SpaceId, out var error));
        Assert.Contains("Malformed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_BadSignature_Fails()
    {
        var jwt = MintJwt(SpaceId, TokenId, TokenSecret, expOffsetSeconds: 3600);
        var parts = jwt.Split('.');
        var bad = parts[0] + "." + parts[1] + "." + Base64UrlEncode(Encoding.UTF8.GetBytes("tampered"));
        Assert.False(_validator.TryValidate(bad, SpaceId, out var error));
        Assert.Contains("signature", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidate_WrongSubject_Fails()
    {
        var jwt = MintJwt(SpaceId, TokenId, TokenSecret, expOffsetSeconds: 3600);
        Assert.False(_validator.TryValidate(jwt, Guid.NewGuid(), out var error));
        Assert.Contains("subject", error, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void TryValidate_ReturnsPermissionsClaim()
    {
        var jwt = MintJwt(SpaceId, TokenId, TokenSecret, expOffsetSeconds: 3600, permissions: ["console.output", "console.send"]);
        Assert.True(_validator.TryValidate(jwt, SpaceId, out var error, out var permissions), error);
        Assert.Contains("console.output", permissions);
        Assert.Contains("console.send", permissions);
    }

    [Fact]
    public void TryValidate_MissingPermissionsClaim_DefaultsToWildcard()
    {
        var jwt = MintJwt(SpaceId, TokenId, TokenSecret, expOffsetSeconds: 3600);
        Assert.True(_validator.TryValidate(jwt, SpaceId, out var error, out var permissions), error);
        Assert.Contains(ConsolePermissions.Wildcard, permissions);
    }

    private static string MintJwt(Guid sub, string kid, string secret, int expOffsetSeconds, string[]? permissions = null)
    {
        var header = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT", kid });
        object payloadObj = permissions is null
            ? new { sub = sub.ToString(), exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expOffsetSeconds }
            : new { sub = sub.ToString(), exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expOffsetSeconds, permissions };
        var payload = JsonSerializer.Serialize(payloadObj);
        var h = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var p = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = Encoding.UTF8.GetBytes(h + "." + p);
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signingInput);
        return h + "." + p + "." + Base64UrlEncode(sig);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
