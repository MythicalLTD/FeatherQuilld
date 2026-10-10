using System.Security.Cryptography;
using System.Text;
using d0x2a.EmbeddedSsh.Crypto;
using d0x2a.EmbeddedSsh.Protocol;

namespace d0x2a.EmbeddedSsh.Auth;

/// <summary>
/// Authenticator using authorized_keys files (OpenSSH format).
/// Supports ssh-ed25519 and ssh-rsa public keys.
/// </summary>
public sealed class AuthorizedKeysAuthenticator : IAuthenticator
{
    private readonly Func<string, ValueTask<IEnumerable<AuthorizedKey>>> _keyProvider;

    /// <summary>
    /// Creates an authenticator with a custom key provider.
    /// </summary>
    /// <param name="keyProvider">Function that returns authorized keys for a username.</param>
    public AuthorizedKeysAuthenticator(Func<string, ValueTask<IEnumerable<AuthorizedKey>>> keyProvider)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
    }

    /// <summary>
    /// Creates an authenticator that reads keys from a dictionary.
    /// </summary>
    /// <param name="userKeys">Dictionary mapping usernames to their authorized keys.</param>
    public AuthorizedKeysAuthenticator(IDictionary<string, IEnumerable<AuthorizedKey>> userKeys)
    {
        ArgumentNullException.ThrowIfNull(userKeys);
        _keyProvider = username =>
        {
            if (userKeys.TryGetValue(username, out var keys))
                return ValueTask.FromResult(keys);
            return ValueTask.FromResult(Enumerable.Empty<AuthorizedKey>());
        };
    }

    public IEnumerable<string> SupportedMethods => ["publickey"];

    public async ValueTask<(AuthResult Result, AuthenticatedUser? User)> AuthenticateAsync(
        AuthContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Method != "publickey")
            return (AuthResult.Failure, null);

        if (!IsSupportedAlgorithm(context.PublicKeyAlgorithm))
            return (AuthResult.Failure, null);

        if (context.PublicKeyBlob == null)
            return (AuthResult.Failure, null);

        // Get authorized keys for this user
        var authorizedKeys = await _keyProvider(context.Username).ConfigureAwait(false);

        // Find matching key
        // Note: RSA keys in authorized_keys are stored as "ssh-rsa" but may be offered
        // with algorithm "rsa-sha2-256" or "rsa-sha2-512" during authentication (RFC 8332)
        AuthorizedKey? matchingKey = null;
        foreach (var key in authorizedKeys)
        {
            if (AreAlgorithmsCompatible(key.Algorithm, context.PublicKeyAlgorithm) &&
                CryptographicOperations.FixedTimeEquals(key.Blob, context.PublicKeyBlob))
            {
                matchingKey = key;
                break;
            }
        }

        if (matchingKey == null)
            return (AuthResult.Failure, null);

        // If no signature, this is just a key check (we'll return failure, but send PK_OK separately)
        if (!context.HasSignature || context.Signature == null)
            return (AuthResult.Failure, null);

        // Verify signature
        var signedData = AuthLayer.BuildPublicKeySignatureData(
            context.SessionId,
            context.Username,
            context.ServiceName,
            context.PublicKeyAlgorithm,
            context.PublicKeyBlob);

        // Verify based on algorithm type
        if (context.PublicKeyAlgorithm == "ssh-ed25519")
        {
            // Extract the actual Ed25519 public key from the blob
            // Format: string "ssh-ed25519" || string public_key (32 bytes)
            var publicKey = ExtractEd25519PublicKey(context.PublicKeyBlob);
            if (publicKey == null)
                return (AuthResult.Failure, null);

            if (!AuthLayer.VerifyEd25519Signature(publicKey, context.Signature, signedData))
                return (AuthResult.Failure, null);
        }
        else if (IsRsaAlgorithm(context.PublicKeyAlgorithm))
        {
            // Extract RSA public key components from the blob
            // Format: string "ssh-rsa" || mpint e || mpint n
            var rsaKey = ExtractRsaPublicKey(context.PublicKeyBlob);
            if (rsaKey == null)
                return (AuthResult.Failure, null);

            if (!AuthLayer.VerifyRsaSignature(rsaKey.Value.Exponent, rsaKey.Value.Modulus, context.Signature, signedData))
                return (AuthResult.Failure, null);
        }

        // Authentication succeeded
        var user = new AuthenticatedUser
        {
            Username = context.Username,
            Method = "publickey",
            Properties = matchingKey.Options != null
                ? new Dictionary<string, object> { ["key_comment"] = matchingKey.Comment ?? "", ["key_options"] = matchingKey.Options }
                : null
        };

        return (AuthResult.Success, user);
    }

    public async ValueTask<bool> IsPublicKeyAcceptableAsync(
        string username,
        string algorithm,
        ReadOnlyMemory<byte> publicKeyBlob,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedAlgorithm(algorithm))
            return false;

        var authorizedKeys = await _keyProvider(username).ConfigureAwait(false);

        foreach (var key in authorizedKeys)
        {
            if (AreAlgorithmsCompatible(key.Algorithm, algorithm) &&
                CryptographicOperations.FixedTimeEquals(key.Blob, publicKeyBlob.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the 32-byte Ed25519 public key from an SSH public key blob.
    /// </summary>
    private static byte[]? ExtractEd25519PublicKey(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 51) // minimum: 4 + 11 + 4 + 32
            return null;

        var reader = new SshReader(blob);
        var algorithm = reader.ReadString();
        if (algorithm != "ssh-ed25519")
            return null;

        var keyData = reader.ReadBinaryString();
        if (keyData.Length != 32)
            return null;

        return keyData.ToArray();
    }

    /// <summary>
    /// Extracts the RSA public key components from an SSH public key blob.
    /// Format: string "ssh-rsa" || mpint e || mpint n
    /// </summary>
    private static (byte[] Exponent, byte[] Modulus)? ExtractRsaPublicKey(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 15) // minimum: 4 + 7 ("ssh-rsa") + 4 + minimum e/n
            return null;

        var reader = new SshReader(blob);
        var algorithm = reader.ReadString();
        if (algorithm != "ssh-rsa")
            return null;

        var exponent = reader.ReadBinaryString();
        var modulus = reader.ReadBinaryString();

        return (exponent.ToArray(), modulus.ToArray());
    }

    /// <summary>
    /// Checks if the given algorithm is supported for authentication.
    /// </summary>
    private static bool IsSupportedAlgorithm(string? algorithm)
    {
        return algorithm == "ssh-ed25519" || IsRsaAlgorithm(algorithm);
    }

    /// <summary>
    /// Checks if the algorithm is an RSA variant.
    /// </summary>
    private static bool IsRsaAlgorithm(string? algorithm)
    {
        return algorithm == "ssh-rsa" || algorithm == "rsa-sha2-256" || algorithm == "rsa-sha2-512";
    }

    /// <summary>
    /// Checks if two algorithms are compatible (e.g., ssh-rsa key can be used with rsa-sha2-256 auth).
    /// </summary>
    private static bool AreAlgorithmsCompatible(string keyAlgorithm, string? authAlgorithm)
    {
        if (keyAlgorithm == authAlgorithm)
            return true;

        // RSA keys stored as "ssh-rsa" can authenticate with rsa-sha2-256 or rsa-sha2-512
        if (keyAlgorithm == "ssh-rsa" && IsRsaAlgorithm(authAlgorithm))
            return true;

        return false;
    }

    /// <summary>
    /// Parses an authorized_keys file content.
    /// </summary>
    public static IEnumerable<AuthorizedKey> ParseAuthorizedKeysFile(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var key = ParseAuthorizedKeyLine(trimmed);
            if (key != null)
                yield return key;
        }
    }

    /// <summary>
    /// Parses a single authorized_keys line.
    /// Format: [options] algorithm base64-key [comment]
    /// </summary>
    public static AuthorizedKey? ParseAuthorizedKeyLine(string line)
    {
        // Simple parser - doesn't handle all edge cases with quoted options
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        int index = 0;
        string? options = null;

        // Check if first part is algorithm or options
        if (!IsKnownAlgorithm(parts[0]))
        {
            options = parts[0];
            index++;
        }

        if (index + 1 >= parts.Length)
            return null;

        var algorithm = parts[index];
        var base64Key = parts[index + 1];
        var comment = index + 2 < parts.Length
            ? string.Join(" ", parts.Skip(index + 2))
            : null;

        // Decode base64
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64Key);
        }
        catch
        {
            return null;
        }

        return new AuthorizedKey(algorithm, blob, comment, options);
    }

    private static bool IsKnownAlgorithm(string s)
    {
        return s == "ssh-ed25519" ||
               s == "ssh-rsa" ||
               s == "ecdsa-sha2-nistp256" ||
               s == "ecdsa-sha2-nistp384" ||
               s == "ecdsa-sha2-nistp521" ||
               s == "ssh-dss" ||
               s.StartsWith("sk-");
    }
}

/// <summary>
/// Represents an authorized SSH public key.
/// </summary>
public sealed record AuthorizedKey(
    string Algorithm,
    byte[] Blob,
    string? Comment = null,
    string? Options = null);
