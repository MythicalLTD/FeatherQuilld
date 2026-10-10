namespace d0x2a.EmbeddedSsh.Transport.Algorithms;

/// <summary>
/// Result of a server-side key exchange operation.
/// </summary>
/// <param name="ServerEphemeral">Server's ephemeral data to send to the client.</param>
/// <param name="SharedSecret">Computed shared secret K.</param>
public readonly record struct KexExchangeResult(byte[] ServerEphemeral, byte[] SharedSecret);

/// <summary>
/// How the shared secret K is encoded in exchange hash and key derivation.
/// </summary>
public enum SharedSecretEncoding
{
    /// <summary>K encoded as mpint (curve25519-sha256).</summary>
    Mpint,

    /// <summary>K encoded as string (mlkem768x25519-sha256).</summary>
    String
}

/// <summary>
/// Interface for SSH key exchange algorithms.
/// </summary>
public interface IKexAlgorithm
{
    /// <summary>
    /// Gets the algorithm name as used in SSH protocol.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets how the shared secret K should be encoded.
    /// </summary>
    SharedSecretEncoding SharedSecretEncoding { get; }

    /// <summary>
    /// Performs the server side of the key exchange given the client's ephemeral data.
    /// </summary>
    /// <param name="clientEphemeral">Client's ephemeral public key or encapsulation data.</param>
    /// <returns>Server's ephemeral data and the shared secret.</returns>
    KexExchangeResult ServerExchange(ReadOnlySpan<byte> clientEphemeral);

    /// <summary>
    /// Computes the exchange hash H using SHA-256.
    /// </summary>
    /// <param name="clientVersion">Client version string (without CRLF).</param>
    /// <param name="serverVersion">Server version string (without CRLF).</param>
    /// <param name="clientKexInit">Client's SSH_MSG_KEXINIT payload.</param>
    /// <param name="serverKexInit">Server's SSH_MSG_KEXINIT payload.</param>
    /// <param name="hostKeyBlob">Server's host key blob.</param>
    /// <param name="clientEphemeral">Client's ephemeral public key.</param>
    /// <param name="serverEphemeral">Server's ephemeral public key.</param>
    /// <param name="sharedSecret">Computed shared secret.</param>
    /// <returns>Exchange hash H (32 bytes for SHA-256).</returns>
    byte[] ComputeExchangeHash(
        ReadOnlySpan<byte> clientVersion,
        ReadOnlySpan<byte> serverVersion,
        ReadOnlySpan<byte> clientKexInit,
        ReadOnlySpan<byte> serverKexInit,
        ReadOnlySpan<byte> hostKeyBlob,
        ReadOnlySpan<byte> clientEphemeral,
        ReadOnlySpan<byte> serverEphemeral,
        ReadOnlySpan<byte> sharedSecret);
}
