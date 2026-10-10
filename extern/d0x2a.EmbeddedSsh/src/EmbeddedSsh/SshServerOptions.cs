using d0x2a.EmbeddedSsh.Auth;
using d0x2a.EmbeddedSsh.Connection;
using d0x2a.EmbeddedSsh.HostKeys;
using d0x2a.EmbeddedSsh.Protocol.Messages;

namespace d0x2a.EmbeddedSsh;

/// <summary>
/// Configuration options for the SSH server.
/// </summary>
public sealed class SshServerOptions
{
    /// <summary>
    /// The server version string sent during protocol negotiation.
    /// Must start with "SSH-2.0-".
    /// </summary>
    public string ServerVersion { get; set; } = "SSH-2.0-EmbeddedSsh_1.0";

    /// <summary>
    /// Host keys for the server. At least one is required.
    /// </summary>
    public List<IHostKey> HostKeys { get; } = [];

    /// <summary>
    /// The authenticator to use for user authentication.
    /// </summary>
    public IAuthenticator? Authenticator { get; set; }

    /// <summary>
    /// Optional handler for channel requests that the connection layer does not
    /// handle natively (notably <c>subsystem</c>). Installed on every
    /// <see cref="ConnectionLayer"/> at construction time so callers do not
    /// have to race the client's post-auth channel requests.
    /// Return <c>true</c> to accept the request (CHANNEL_SUCCESS), <c>false</c>
    /// to reject it (CHANNEL_FAILURE).
    /// </summary>
    public Func<SshChannel, ChannelRequestMessage, CancellationToken, ValueTask<bool>>? ChannelRequestHandler { get; set; }

    /// <summary>
    /// Maximum number of authentication attempts per connection.
    /// </summary>
    public int MaxAuthAttempts { get; set; } = 20;

    /// <summary>
    /// Connection timeout for initial handshake (version exchange + key exchange).
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Authentication timeout.
    /// </summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Idle timeout for authenticated sessions.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum number of concurrent connections.
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Whether to send a banner before authentication.
    /// </summary>
    public string? Banner { get; set; }

    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (!ServerVersion.StartsWith("SSH-2.0-"))
            throw new ArgumentException("ServerVersion must start with SSH-2.0-");

        if (HostKeys.Count == 0)
            throw new ArgumentException("At least one host key is required");

        if (Authenticator == null)
            throw new ArgumentException("Authenticator is required");

        if (MaxAuthAttempts <= 0)
            throw new ArgumentException("MaxAuthAttempts must be positive");

        if (MaxConnections <= 0)
            throw new ArgumentException("MaxConnections must be positive");
    }

    /// <summary>
    /// Gets the host key for the specified algorithm.
    /// </summary>
    public IHostKey? GetHostKey(string algorithm)
    {
        return HostKeys.FirstOrDefault(k => k.Algorithm == algorithm);
    }
}
