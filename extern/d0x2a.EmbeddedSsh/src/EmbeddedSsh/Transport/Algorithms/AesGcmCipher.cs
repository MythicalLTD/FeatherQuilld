using System.Buffers.Binary;
using System.Security.Cryptography;

namespace d0x2a.EmbeddedSsh.Transport.Algorithms;

/// <summary>
/// AES-256-GCM AEAD cipher for SSH (aes256-gcm@openssh.com).
///
/// Per RFC 5647:
/// - Packet length is NOT encrypted but IS authenticated (as AAD)
/// - Nonce: 12 bytes, constructed from IV and invocation counter
/// - Plaintext: padding_length || payload || padding
/// - Tag: 16 bytes
/// </summary>
public sealed class AesGcmCipher : ISshCipher, IDisposable
{
    private AesGcm _aesGcm = null!;
    private byte[] _fixedField = null!;  // First 4 bytes of IV (fixed)
    private ulong _invocationCounter;     // Last 8 bytes of IV + increments
    private bool _disposed;

    public string Name => "aes256-gcm@openssh.com";
    public int KeySize => 32;   // 256 bits
    public int IvSize => 12;    // 96-bit nonce
    public int TagSize => 16;   // 128-bit tag
    public bool IsAead => true;
    public int BlockSize => 16; // AES block size

    public void Initialize(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes", nameof(key));
        if (iv.Length != IvSize)
            throw new ArgumentException($"IV must be {IvSize} bytes", nameof(iv));

        _aesGcm = new AesGcm(key, TagSize);

        // Per RFC 5647: IV = fixed field (4 bytes) || invocation counter (8 bytes)
        // The invocation counter starts at the value from the IV and increments
        _fixedField = iv[..4].ToArray();
        _invocationCounter = BinaryPrimitives.ReadUInt64BigEndian(iv[4..]);
    }

    /// <summary>
    /// Builds the 12-byte nonce from fixed field and invocation counter.
    /// Per RFC 5647: nonce = fixed field (4 bytes) || invocation counter (8 bytes)
    /// The counter starts from the IV value and increments for each packet.
    /// </summary>
    private void BuildNonce(Span<byte> nonce)
    {
        // Fixed field (first 4 bytes)
        _fixedField.CopyTo(nonce[..4]);

        // Invocation counter (last 8 bytes, big-endian)
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _invocationCounter);

        _invocationCounter++;
    }

    public uint DecryptLength(uint sequenceNumber, ReadOnlySpan<byte> encryptedLength)
    {
        // In AES-GCM, the length is NOT encrypted - just read it directly
        return BinaryPrimitives.ReadUInt32BigEndian(encryptedLength);
    }

    public int Encrypt(uint sequenceNumber, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext)
    {
        // plaintext = packet_length (4) || padding_length (1) || payload || padding
        // Output = packet_length (4, plaintext) || encrypted(rest) || tag (16)
        //
        // In SSH AES-GCM:
        // - packet_length is NOT encrypted, but authenticated as AAD
        // - The rest (padding_length || payload || padding) is encrypted

        if (plaintext.Length < 5)
            throw new ArgumentException("Plaintext too short", nameof(plaintext));

        var payloadLen = plaintext.Length - 4;  // Everything after packet_length
        var totalOutput = 4 + payloadLen + TagSize;

        if (ciphertext.Length < totalOutput)
            throw new ArgumentException("Ciphertext buffer too small", nameof(ciphertext));

        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce);

        // AAD = packet_length (4 bytes, unencrypted)
        var aad = plaintext[..4];

        // Copy packet_length to output (unencrypted)
        aad.CopyTo(ciphertext[..4]);

        // Encrypt the payload part
        var plaintextPayload = plaintext[4..];
        var ciphertextPayload = ciphertext[4..(4 + payloadLen)];
        var tag = ciphertext[(4 + payloadLen)..(4 + payloadLen + TagSize)];

        _aesGcm.Encrypt(nonce, plaintextPayload, ciphertextPayload, tag, aad);

        return totalOutput;
    }

    public int Decrypt(uint sequenceNumber, ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        // ciphertext = packet_length (4, plaintext) || encrypted_payload || tag (16)
        // Output = packet_length (4) || padding_length (1) || payload || padding

        if (ciphertext.Length < 4 + TagSize)
            throw new ArgumentException("Ciphertext too short", nameof(ciphertext));

        var encryptedPayloadLen = ciphertext.Length - 4 - TagSize;
        var totalOutput = 4 + encryptedPayloadLen;

        if (plaintext.Length < totalOutput)
            throw new ArgumentException("Plaintext buffer too small", nameof(plaintext));

        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(nonce);

        // AAD = packet_length (4 bytes)
        var aad = ciphertext[..4];

        // Copy packet_length to output
        aad.CopyTo(plaintext[..4]);

        var encryptedPayload = ciphertext[4..(4 + encryptedPayloadLen)];
        var tag = ciphertext[(4 + encryptedPayloadLen)..];
        var decryptedPayload = plaintext[4..(4 + encryptedPayloadLen)];

        try
        {
            _aesGcm.Decrypt(nonce, encryptedPayload, tag, decryptedPayload, aad);
            return totalOutput;
        }
        catch (AuthenticationTagMismatchException)
        {
            return -1;  // Authentication failed
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _aesGcm?.Dispose();
        if (_fixedField != null)
            CryptographicOperations.ZeroMemory(_fixedField);

        _disposed = true;
    }
}
