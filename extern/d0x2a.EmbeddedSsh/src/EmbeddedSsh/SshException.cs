namespace d0x2a.EmbeddedSsh;

/// <summary>
/// Base exception for all SSH-related errors.
/// </summary>
public class SshException : Exception
{
    public SshException(string message) : base(message) { }
    public SshException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exception thrown when an SSH protocol violation is detected.
/// </summary>
public class SshProtocolException : SshException
{
    /// <summary>
    /// The disconnect reason code to send to the peer.
    /// </summary>
    public DisconnectReason Reason { get; }

    public SshProtocolException(string message)
        : base(message)
    {
        Reason = DisconnectReason.ProtocolError;
    }

    public SshProtocolException(DisconnectReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}

/// <summary>
/// Exception thrown when authentication fails.
/// </summary>
public class SshAuthenticationException(string username, string method, string message) : SshException(message)
{
    /// <summary>
    /// The username that failed to authenticate.
    /// </summary>
    public string Username { get; } = username;

    /// <summary>
    /// The authentication method that failed.
    /// </summary>
    public string Method { get; } = method;
}

/// <summary>
/// Exception thrown when a cryptographic operation fails.
/// </summary>
public class SshCryptoException : SshException
{
    public SshCryptoException(string message) : base(message) { }
    public SshCryptoException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// SSH disconnect reason codes (RFC 4253 §11.1).
/// </summary>
public enum DisconnectReason : uint
{
    HostNotAllowedToConnect = 1,
    ProtocolError = 2,
    KeyExchangeFailed = 3,
    Reserved = 4,
    MacError = 5,
    CompressionError = 6,
    ServiceNotAvailable = 7,
    ProtocolVersionNotSupported = 8,
    HostKeyNotVerifiable = 9,
    ConnectionLost = 10,
    ByApplication = 11,
    TooManyConnections = 12,
    AuthCancelledByUser = 13,
    NoMoreAuthMethodsAvailable = 14,
    IllegalUserName = 15,
}
