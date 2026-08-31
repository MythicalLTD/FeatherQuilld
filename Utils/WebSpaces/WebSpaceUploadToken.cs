using System.Security.Cryptography;
using System.Text;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.WebSpaces;

public sealed class WebSpaceUploadTokenPayload
{
    public Guid WebSpaceUuid { get; init; }
    public string Directory { get; init; } = "/";
    public string FileName { get; init; } = "";
}

public static class WebSpaceUploadToken
{
    public static string Create(AppConfig config, Guid uuid, string directory, string? fileName, TimeSpan ttl)
    {
        var exp = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var payload = BuildPayload(uuid, directory, fileName, exp);
        var sig = Sign(config.BearerToken, payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "." + sig;
    }

    public static bool TryValidate(string token, Guid uuid, out WebSpaceUploadTokenPayload payload)
    {
        payload = new WebSpaceUploadTokenPayload();
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split('.', 2);
        if (parts.Length != 2)
            return false;

        string raw;
        try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
        catch { return false; }

        var segments = raw.Split('|', 4);
        if (segments.Length != 4)
            return false;
        if (!Guid.TryParse(segments[0], out var tokenUuid) || tokenUuid != uuid)
            return false;
        if (!long.TryParse(segments[3], out var exp) || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;

        // Signature validated by caller with config — static helper used from controller with injected config
        payload = new WebSpaceUploadTokenPayload
        {
            WebSpaceUuid = tokenUuid,
            Directory = segments[1],
            FileName = segments[2],
        };
        return true;
    }

    public static bool ValidateSignature(AppConfig config, string token)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2)
            return false;
        string raw;
        try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
        catch { return false; }
        return string.Equals(Sign(config.BearerToken, raw), parts[1], StringComparison.Ordinal);
    }

    private static string BuildPayload(Guid uuid, string directory, string? fileName, long exp) =>
        $"{uuid}|{directory}|{fileName ?? ""}|{exp}";

    private static string Sign(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
