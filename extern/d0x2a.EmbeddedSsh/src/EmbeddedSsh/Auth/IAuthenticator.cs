namespace d0x2a.EmbeddedSsh.Auth;

/// <summary>
/// Result of an authentication attempt.
/// </summary>
public enum AuthResult
{
    /// <summary>
    /// Authentication succeeded.
    /// </summary>
    Success,

    /// <summary>
    /// Authentication failed.
    /// </summary>
    Failure,

    /// <summary>
    /// Partial success (for multi-factor authentication).
    /// </summary>
    Partial,

    /// <summary>
    /// Continue with the specified challenge data (for keyboard-interactive, etc.).
    /// </summary>
    Continue
}

/// <summary>
/// Information about an authenticated user.
/// </summary>
public sealed class AuthenticatedUser
{
    /// <summary>
    /// The authenticated username.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// The authentication method used (e.g., "publickey", "password").
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Additional user properties (e.g., home directory, shell).
    /// </summary>
    public IDictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Context for authentication requests.
/// </summary>
public sealed class AuthContext
{
    /// <summary>
    /// The username being authenticated.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// The requested service name (e.g., "ssh-connection").
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    /// The authentication method (e.g., "publickey", "password", "none").
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// The session identifier.
    /// </summary>
    public required byte[] SessionId { get; init; }

    /// <summary>
    /// For publickey auth: the public key algorithm name.
    /// </summary>
    public string? PublicKeyAlgorithm { get; init; }

    /// <summary>
    /// For publickey auth: the public key blob.
    /// </summary>
    public byte[]? PublicKeyBlob { get; init; }

    /// <summary>
    /// For publickey auth: the signature (null for key checking only).
    /// </summary>
    public byte[]? Signature { get; init; }

    /// <summary>
    /// For publickey auth: whether this is a signature verification request.
    /// </summary>
    public bool HasSignature { get; init; }

    /// <summary>
    /// For password auth: the password.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// Interface for SSH authentication providers.
/// </summary>
public interface IAuthenticator
{
    /// <summary>
    /// Gets the authentication methods supported by this authenticator.
    /// </summary>
    IEnumerable<string> SupportedMethods { get; }

    /// <summary>
    /// Attempts to authenticate a user.
    /// </summary>
    /// <param name="context">Authentication context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result and authenticated user (if successful).</returns>
    ValueTask<(AuthResult Result, AuthenticatedUser? User)> AuthenticateAsync(
        AuthContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a public key is acceptable for the given user (before signature verification).
    /// This allows the server to respond with SSH_MSG_USERAUTH_PK_OK before requiring a signature.
    /// </summary>
    /// <param name="username">Username.</param>
    /// <param name="algorithm">Public key algorithm.</param>
    /// <param name="publicKeyBlob">Public key blob.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the key might be acceptable.</returns>
    ValueTask<bool> IsPublicKeyAcceptableAsync(
        string username,
        string algorithm,
        ReadOnlyMemory<byte> publicKeyBlob,
        CancellationToken cancellationToken = default);
}
