namespace d0x2a.EmbeddedSsh.Transport.Algorithms;

/// <summary>
/// Interface for SSH encryption algorithms.
/// </summary>
public interface ISshCipher
{
    /// <summary>
    /// Gets the algorithm name as used in SSH protocol.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the key size in bytes.
    /// </summary>
    int KeySize { get; }

    /// <summary>
    /// Gets the IV/nonce size in bytes.
    /// </summary>
    int IvSize { get; }

    /// <summary>
    /// Gets the authentication tag size in bytes (0 for non-AEAD ciphers).
    /// </summary>
    int TagSize { get; }

    /// <summary>
    /// Gets whether this is an AEAD cipher.
    /// </summary>
    bool IsAead { get; }

    /// <summary>
    /// Gets the block size in bytes for padding alignment.
    /// </summary>
    int BlockSize { get; }

    /// <summary>
    /// Initializes the cipher with key material.
    /// </summary>
    /// <param name="key">Encryption key.</param>
    /// <param name="iv">Initial IV/nonce.</param>
    void Initialize(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv);

    /// <summary>
    /// Encrypts an SSH packet.
    /// For AEAD ciphers, includes authentication.
    /// </summary>
    /// <param name="sequenceNumber">Packet sequence number.</param>
    /// <param name="plaintext">Plaintext packet data (length + padding_length + payload + padding).</param>
    /// <param name="ciphertext">Output buffer for ciphertext (may include tag).</param>
    /// <returns>Number of bytes written to ciphertext.</returns>
    int Encrypt(uint sequenceNumber, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext);

    /// <summary>
    /// Decrypts an SSH packet.
    /// For AEAD ciphers, includes authentication verification.
    /// </summary>
    /// <param name="sequenceNumber">Packet sequence number.</param>
    /// <param name="ciphertext">Ciphertext packet data (may include tag).</param>
    /// <param name="plaintext">Output buffer for plaintext.</param>
    /// <returns>Number of bytes written to plaintext, or -1 if authentication failed.</returns>
    int Decrypt(uint sequenceNumber, ReadOnlySpan<byte> ciphertext, Span<byte> plaintext);

    /// <summary>
    /// For AEAD ciphers: decrypts just the packet length field.
    /// </summary>
    /// <param name="sequenceNumber">Packet sequence number.</param>
    /// <param name="encryptedLength">4-byte encrypted length.</param>
    /// <returns>Decrypted packet length.</returns>
    uint DecryptLength(uint sequenceNumber, ReadOnlySpan<byte> encryptedLength);
}

/// <summary>
/// Null cipher for unencrypted transport (during key exchange).
/// </summary>
public sealed class NullCipher : ISshCipher
{
    public static readonly NullCipher Instance = new();

    public string Name => "none";
    public int KeySize => 0;
    public int IvSize => 0;
    public int TagSize => 0;
    public bool IsAead => false;
    public int BlockSize => 8;  // Minimum per RFC 4253

    public void Initialize(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv) { }

    public int Encrypt(uint sequenceNumber, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext)
    {
        plaintext.CopyTo(ciphertext);
        return plaintext.Length;
    }

    public int Decrypt(uint sequenceNumber, ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        ciphertext.CopyTo(plaintext);
        return ciphertext.Length;
    }

    public uint DecryptLength(uint sequenceNumber, ReadOnlySpan<byte> encryptedLength)
    {
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(encryptedLength);
    }
}
