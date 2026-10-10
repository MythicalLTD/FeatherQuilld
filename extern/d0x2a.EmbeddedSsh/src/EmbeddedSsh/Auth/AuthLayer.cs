using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Crypto;
using d0x2a.EmbeddedSsh.Protocol;
using d0x2a.EmbeddedSsh.Protocol.Messages;
using d0x2a.EmbeddedSsh.Transport;

namespace d0x2a.EmbeddedSsh.Auth;

/// <summary>
/// SSH authentication layer (RFC 4252).
/// Handles SSH_MSG_USERAUTH_* message processing.
/// </summary>
public sealed class AuthLayer
{
    private readonly TransportLayer _transport;
    private readonly IAuthenticator _authenticator;
    private readonly int _maxAttempts;

    private int _attemptCount;
    private AuthenticatedUser? _authenticatedUser;

    public AuthLayer(TransportLayer transport, IAuthenticator authenticator, int maxAttempts = 20)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// Gets the authenticated user, if authentication has succeeded.
    /// </summary>
    public AuthenticatedUser? AuthenticatedUser => _authenticatedUser;

    /// <summary>
    /// Gets whether authentication has succeeded.
    /// </summary>
    public bool IsAuthenticated => _authenticatedUser != null;

    /// <summary>
    /// Processes authentication messages until success or failure.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authenticated user.</returns>
    public async ValueTask<AuthenticatedUser> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        while (!IsAuthenticated)
        {
            var message = await _transport.ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);

            switch (message)
            {
                case UserauthRequestMessage request:
                    await ProcessAuthRequestAsync(request, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new SshProtocolException(DisconnectReason.ProtocolError,
                        $"Unexpected message during authentication: {message.MessageType}");
            }

            if (_attemptCount >= _maxAttempts)
                throw new SshAuthenticationException("unknown", "unknown", "Too many authentication attempts");
        }

        return _authenticatedUser!;
    }

    private async ValueTask ProcessAuthRequestAsync(UserauthRequestMessage request, CancellationToken cancellationToken)
    {
        _attemptCount++;

        var context = BuildAuthContext(request);
        var (result, user) = await _authenticator.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);

        switch (result)
        {
            case AuthResult.Success:
                _authenticatedUser = user;
                await _transport.SendMessageAsync(UserauthSuccessMessage.Instance, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case AuthResult.Partial:
                // Multi-factor auth: more methods required
                await SendFailureAsync(partial: true, cancellationToken).ConfigureAwait(false);
                break;

            case AuthResult.Failure:
                // For publickey without signature, check if key is acceptable
                if (request.MethodName == "publickey" && !request.HasSignature && request.PublicKeyBlob.HasValue)
                {
                    var acceptable = await _authenticator.IsPublicKeyAcceptableAsync(
                        request.Username,
                        request.PublicKeyAlgorithm!,
                        request.PublicKeyBlob.Value,
                        cancellationToken).ConfigureAwait(false);

                    if (acceptable)
                    {
                        // Send PK_OK to request signature
                        await SendPublicKeyOkAsync(request.PublicKeyAlgorithm!, request.PublicKeyBlob.Value, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                }

                await SendFailureAsync(partial: false, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await SendFailureAsync(partial: false, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private AuthContext BuildAuthContext(UserauthRequestMessage request)
    {
        return new AuthContext
        {
            Username = request.Username,
            ServiceName = request.ServiceName,
            Method = request.MethodName,
            SessionId = _transport.SessionId.ToArray(),
            PublicKeyAlgorithm = request.PublicKeyAlgorithm,
            PublicKeyBlob = request.PublicKeyBlob.HasValue ? request.PublicKeyBlob.Value.ToArray() : null,
            Signature = request.Signature.HasValue ? request.Signature.Value.ToArray() : null,
            HasSignature = request.HasSignature,
            Password = request.Password
        };
    }

    private async ValueTask SendFailureAsync(bool partial, CancellationToken cancellationToken)
    {
        var methods = _authenticator.SupportedMethods.ToList();
        var message = new UserauthFailureMessage
        {
            AuthenticationsThatCanContinue = methods,
            PartialSuccess = partial
        };
        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendPublicKeyOkAsync(string algorithm, ReadOnlyMemory<byte> publicKeyBlob, CancellationToken cancellationToken)
    {
        var message = new UserauthPkOkMessage
        {
            Algorithm = algorithm,
            PublicKeyBlob = publicKeyBlob
        };
        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the data that was signed for public key authentication verification.
    /// Format: session_id (string) || byte SSH_MSG_USERAUTH_REQUEST || user name (string)
    ///         || service name (string) || "publickey" (string) || TRUE (boolean)
    ///         || public key algorithm name (string) || public key blob (string)
    /// </summary>
    public static byte[] BuildPublicKeySignatureData(
        ReadOnlySpan<byte> sessionId,
        string username,
        string serviceName,
        string algorithm,
        ReadOnlySpan<byte> publicKeyBlob)
    {
        // Calculate total size
        var size = 4 + sessionId.Length +           // session_id as string
                   1 +                               // message type byte
                   4 + username.Length +             // username
                   4 + serviceName.Length +          // service name
                   4 + 9 +                           // "publickey"
                   1 +                               // boolean TRUE
                   4 + algorithm.Length +            // algorithm name
                   4 + publicKeyBlob.Length;         // public key blob

        var buffer = new byte[size];
        var writer = new SshWriter(buffer);

        writer.WriteBinaryString(sessionId);
        writer.WriteByte((byte)SshMessageType.UserauthRequest);
        writer.WriteString(username);
        writer.WriteString(serviceName);
        writer.WriteString("publickey");
        writer.WriteBoolean(true);
        writer.WriteString(algorithm);
        writer.WriteBinaryString(publicKeyBlob);

        return buffer;
    }

    /// <summary>
    /// Verifies an Ed25519 signature for public key authentication.
    /// </summary>
    public static bool VerifyEd25519Signature(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> signatureBlob,
        ReadOnlySpan<byte> signedData)
    {
        // The signature blob format is: string algorithm || string signature
        if (signatureBlob.Length < 8)
            return false;

        var reader = new SshReader(signatureBlob);

        // Read algorithm name
        var algorithm = reader.ReadString();
        if (algorithm != "ssh-ed25519")
            return false;

        // Read actual signature
        var signature = reader.ReadBinaryString();
        if (signature.Length != 64)
            return false;

        return Ed25519.Verify(publicKey, signedData, signature);
    }

    /// <summary>
    /// Verifies an RSA signature for public key authentication.
    /// Supports rsa-sha2-256, rsa-sha2-512, and legacy ssh-rsa (SHA-1).
    /// </summary>
    public static bool VerifyRsaSignature(
        ReadOnlySpan<byte> exponent,
        ReadOnlySpan<byte> modulus,
        ReadOnlySpan<byte> signatureBlob,
        ReadOnlySpan<byte> signedData)
    {
        if (signatureBlob.Length < 8)
            return false;

        var reader = new SshReader(signatureBlob);

        // Read algorithm name
        var algorithm = reader.ReadString();

        // Determine hash algorithm based on signature type
        HashAlgorithmName hashAlgorithm;
        switch (algorithm)
        {
            case "rsa-sha2-256":
                hashAlgorithm = HashAlgorithmName.SHA256;
                break;
            case "rsa-sha2-512":
                hashAlgorithm = HashAlgorithmName.SHA512;
                break;
            case "ssh-rsa":
                hashAlgorithm = HashAlgorithmName.SHA1;
                break;
            default:
                return false;
        }

        // Read actual signature
        var signature = reader.ReadBinaryString();

        // Verify using .NET RSA
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Exponent = exponent.ToArray(),
            Modulus = modulus.ToArray()
        });

        return rsa.VerifyData(signedData, signature, hashAlgorithm, RSASignaturePadding.Pkcs1);
    }
}
