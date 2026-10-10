using System.Buffers.Binary;
using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Crypto;

namespace d0x2a.EmbeddedSsh.Transport.Algorithms;

/// <summary>
/// ChaCha20-Poly1305 AEAD cipher for SSH (chacha20-poly1305@openssh.com).
///
/// This follows the OpenSSH specification:
/// - Uses two ChaCha20 instances: one for packet length, one for payload
/// - K_1 (first 32 bytes): payload encryption key and Poly1305 key derivation
/// - K_2 (last 32 bytes): length encryption key
/// - Nonce is the packet sequence number as 64-bit big-endian, with 4 zero bytes prepended
/// - Poly1305 key is derived from ChaCha20(K_1, nonce, counter=0)
/// - Poly1305 authenticates: encrypted_length || ciphertext
/// </summary>
public sealed class ChaCha20Poly1305Cipher : ISshCipher, IDisposable
{
    private byte[] _payloadKey = null!;  // K_1: first 32 bytes
    private byte[] _lengthKey = null!;   // K_2: last 32 bytes
    private bool _disposed;

    public string Name => "chacha20-poly1305@openssh.com";

    /// <summary>
    /// Key size is 64 bytes (two 32-byte ChaCha20 keys).
    /// </summary>
    public int KeySize => 64;

    /// <summary>
    /// No separate IV needed; nonce is derived from sequence number.
    /// </summary>
    public int IvSize => 0;

    /// <summary>
    /// Poly1305 tag is 16 bytes.
    /// </summary>
    public int TagSize => 16;

    /// <summary>
    /// This is an AEAD cipher.
    /// </summary>
    public bool IsAead => true;

    /// <summary>
    /// Block size for padding alignment (8 bytes per PROTOCOL.chacha20poly1305).
    /// </summary>
    public int BlockSize => 8;

    public void Initialize(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 64)
            throw new ArgumentException("Key must be 64 bytes", nameof(key));

        // Per OpenSSH cipher-chachapoly.c:
        // chacha_keysetup(&ctx->main_ctx, key, 256);        // bytes 0-31 → K_1
        // chacha_keysetup(&ctx->header_ctx, key + 32, 256); // bytes 32-63 → K_2
        // K_1 = first 32 bytes = main_ctx (payload encryption, Poly1305 key)
        // K_2 = last 32 bytes = header_ctx (length encryption)
        _payloadKey = key[..32].ToArray();   // K_1 for payload/Poly1305
        _lengthKey = key[32..64].ToArray();  // K_2 for length
    }

    /// <summary>
    /// Builds the 8-byte nonce from sequence number (OpenSSH format).
    /// Format: sequence number as 8-byte big-endian
    /// </summary>
    private static void BuildNonce(uint sequenceNumber, Span<byte> nonce)
    {
        BinaryPrimitives.WriteUInt64BigEndian(nonce, sequenceNumber);
    }

    public uint DecryptLength(uint sequenceNumber, ReadOnlySpan<byte> encryptedLength)
    {
        if (encryptedLength.Length != 4)
            throw new ArgumentException("Encrypted length must be 4 bytes", nameof(encryptedLength));

        Span<byte> nonce = stackalloc byte[8];
        BuildNonce(sequenceNumber, nonce);

        // Decrypt length using K_2, counter 0
        Span<byte> decryptedLength = stackalloc byte[4];
        ChaCha20.ProcessOpenSsh(_lengthKey, nonce, 0, encryptedLength, decryptedLength);

        return BinaryPrimitives.ReadUInt32BigEndian(decryptedLength);
    }

    public int Encrypt(uint sequenceNumber, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext)
    {
        // plaintext = packet_length (4) || padding_length (1) || payload || padding
        // Output = encrypted_length (4) || encrypted_payload || tag (16)

        if (plaintext.Length < 5)
            throw new ArgumentException("Plaintext too short", nameof(plaintext));

        var totalOutput = plaintext.Length + TagSize;
        if (ciphertext.Length < totalOutput)
            throw new ArgumentException("Ciphertext buffer too small", nameof(ciphertext));

        Span<byte> nonce = stackalloc byte[8];
        BuildNonce(sequenceNumber, nonce);

        // 1. Encrypt the 4-byte length with K_2, counter 0
        ChaCha20.ProcessOpenSsh(_lengthKey, nonce, 0, plaintext[..4], ciphertext[..4]);

        // 2. Get Poly1305 key from ChaCha20(K_1, nonce, counter=0)
        Span<byte> polyKey = stackalloc byte[64];  // Only first 32 bytes used
        ChaCha20.BlockOpenSsh(_payloadKey, nonce, 0, polyKey);

        // 3. Encrypt the rest (padding_length || payload || padding) with K_1, counter 1
        var payloadLen = plaintext.Length - 4;
        ChaCha20.ProcessOpenSsh(_payloadKey, nonce, 1, plaintext[4..], ciphertext[4..(4 + payloadLen)]);

        // 4. Compute Poly1305 tag over: encrypted_length || encrypted_payload
        var authData = ciphertext[..(4 + payloadLen)];
        Span<byte> tag = stackalloc byte[16];
        Poly1305.ComputeTag(polyKey[..32], authData, tag);

        // 5. Clear sensitive key material
        CryptographicOperations.ZeroMemory(polyKey);

        // 6. Append tag
        tag.CopyTo(ciphertext[(4 + payloadLen)..]);

        return totalOutput;
    }

    public int Decrypt(uint sequenceNumber, ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        // ciphertext = encrypted_length (4) || encrypted_payload || tag (16)
        // Output = packet_length (4) || padding_length (1) || payload || padding

        if (ciphertext.Length < 4 + TagSize)
            throw new ArgumentException("Ciphertext too short", nameof(ciphertext));

        var encryptedLen = 4;
        var tagLen = TagSize;
        var encryptedPayloadLen = ciphertext.Length - encryptedLen - tagLen;

        if (plaintext.Length < encryptedLen + encryptedPayloadLen)
            throw new ArgumentException("Plaintext buffer too small", nameof(plaintext));

        Span<byte> nonce = stackalloc byte[8];
        BuildNonce(sequenceNumber, nonce);

        // 1. Get Poly1305 key from ChaCha20(K_1, nonce, counter=0)
        Span<byte> polyKey = stackalloc byte[64];
        ChaCha20.BlockOpenSsh(_payloadKey, nonce, 0, polyKey);

        // 2. Verify Poly1305 tag over: encrypted_length || encrypted_payload
        var authData = ciphertext[..(encryptedLen + encryptedPayloadLen)];
        var receivedTag = ciphertext[(encryptedLen + encryptedPayloadLen)..];

        if (!Poly1305.Verify(polyKey[..32], authData, receivedTag))
        {
            CryptographicOperations.ZeroMemory(polyKey);
            return -1;  // Authentication failed
        }

        // Clear poly key after verification
        CryptographicOperations.ZeroMemory(polyKey);

        // 3. Decrypt length with K_2, counter 0
        ChaCha20.ProcessOpenSsh(_lengthKey, nonce, 0, ciphertext[..4], plaintext[..4]);

        // 4. Decrypt payload with K_1, counter 1
        ChaCha20.ProcessOpenSsh(_payloadKey, nonce, 1,
            ciphertext[encryptedLen..(encryptedLen + encryptedPayloadLen)],
            plaintext[4..(4 + encryptedPayloadLen)]);

        return encryptedLen + encryptedPayloadLen;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_payloadKey != null)
            CryptographicOperations.ZeroMemory(_payloadKey);
        if (_lengthKey != null)
            CryptographicOperations.ZeroMemory(_lengthKey);

        _disposed = true;
    }
}
