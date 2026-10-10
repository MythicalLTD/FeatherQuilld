namespace d0x2a.EmbeddedSsh.HostKeys;

/// <summary>
/// Interface for SSH host keys.
/// </summary>
public interface IHostKey
{
    /// <summary>
    /// Gets the algorithm name (e.g., "ssh-ed25519").
    /// </summary>
    string Algorithm { get; }

    /// <summary>
    /// Gets the public key blob for the key exchange.
    /// </summary>
    byte[] GetPublicKeyBlob();

    /// <summary>
    /// Signs data using this host key.
    /// </summary>
    /// <param name="data">Data to sign.</param>
    /// <returns>Signature blob (algorithm name + signature).</returns>
    byte[] Sign(ReadOnlySpan<byte> data);
}
