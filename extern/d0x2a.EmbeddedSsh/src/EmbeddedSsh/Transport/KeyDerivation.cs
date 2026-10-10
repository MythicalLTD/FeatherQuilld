using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Protocol;
using d0x2a.EmbeddedSsh.Transport.Algorithms;

namespace d0x2a.EmbeddedSsh.Transport;

/// <summary>
/// SSH key derivation functions (RFC 4253 Section 7.2).
/// Derives encryption keys, IVs, and integrity keys from the shared secret.
/// </summary>
public static class KeyDerivation
{
    /// <summary>
    /// Key derivation identifiers from RFC 4253 Section 7.2.
    /// </summary>
    public static class KeyId
    {
        /// <summary>Initial IV client to server: HASH(K || H || "A" || session_id)</summary>
        public const byte IvClientToServer = (byte)'A';

        /// <summary>Initial IV server to client: HASH(K || H || "B" || session_id)</summary>
        public const byte IvServerToClient = (byte)'B';

        /// <summary>Encryption key client to server: HASH(K || H || "C" || session_id)</summary>
        public const byte EncryptionKeyClientToServer = (byte)'C';

        /// <summary>Encryption key server to client: HASH(K || H || "D" || session_id)</summary>
        public const byte EncryptionKeyServerToClient = (byte)'D';

        /// <summary>Integrity key client to server: HASH(K || H || "E" || session_id)</summary>
        public const byte IntegrityKeyClientToServer = (byte)'E';

        /// <summary>Integrity key server to client: HASH(K || H || "F" || session_id)</summary>
        public const byte IntegrityKeyServerToClient = (byte)'F';
    }

    /// <summary>
    /// Derives a key using the SSH key derivation function.
    /// K_n = HASH(K || H || X || session_id || K_1 || ... || K_{n-1})
    /// </summary>
    /// <param name="sharedSecret">Shared secret K.</param>
    /// <param name="exchangeHash">Exchange hash H.</param>
    /// <param name="keyId">Key identifier character (A-F).</param>
    /// <param name="sessionId">Session identifier (first exchange hash).</param>
    /// <param name="requiredLength">Required key length in bytes.</param>
    /// <param name="encoding">How K is encoded in the hash (mpint or string).</param>
    /// <returns>Derived key material.</returns>
    public static byte[] DeriveKey(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> exchangeHash,
        byte keyId,
        ReadOnlySpan<byte> sessionId,
        int requiredLength,
        SharedSecretEncoding encoding = SharedSecretEncoding.Mpint)
    {
        if (requiredLength <= 0)
            return [];

        var result = new byte[requiredLength];
        DeriveKey(sharedSecret, exchangeHash, keyId, sessionId, result, encoding);
        return result;
    }

    /// <summary>
    /// Derives a key using the SSH key derivation function.
    /// </summary>
    public static void DeriveKey(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> exchangeHash,
        byte keyId,
        ReadOnlySpan<byte> sessionId,
        Span<byte> output,
        SharedSecretEncoding encoding = SharedSecretEncoding.Mpint)
    {
        if (output.Length == 0)
            return;

        // First block: K_1 = HASH(K || H || X || session_id)
        var hashSize = SHA256.HashSizeInBytes; // 32 bytes
        Span<byte> k1 = stackalloc byte[hashSize];

        using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            // Write K
            WriteSharedSecretToHash(sha256, sharedSecret, encoding);

            // Write H
            sha256.AppendData(exchangeHash);

            // Write X (single byte)
            Span<byte> keyIdByte = stackalloc byte[1] { keyId };
            sha256.AppendData(keyIdByte);

            // Write session_id
            sha256.AppendData(sessionId);

            sha256.GetHashAndReset(k1);
        }

        // Copy first block
        var toCopy = Math.Min(hashSize, output.Length);
        k1[..toCopy].CopyTo(output);

        if (output.Length <= hashSize)
            return;

        // Need more key material: K_n = HASH(K || H || K_1 || ... || K_{n-1})
        var offset = hashSize;
        Span<byte> previousBlocks = stackalloc byte[hashSize * 8]; // Support up to 256 bytes
        Span<byte> kn = stackalloc byte[hashSize]; // Moved outside loop to avoid CA2014
        k1.CopyTo(previousBlocks);
        var previousLength = hashSize;

        while (offset < output.Length)
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Write K
            WriteSharedSecretToHash(sha256, sharedSecret, encoding);

            // Write H
            sha256.AppendData(exchangeHash);

            // Write all previous K blocks
            sha256.AppendData(previousBlocks[..previousLength]);

            sha256.GetHashAndReset(kn);

            // Append to previous blocks for next iteration
            kn.CopyTo(previousBlocks.Slice(previousLength, hashSize));
            previousLength += hashSize;

            // Copy to output
            toCopy = Math.Min(hashSize, output.Length - offset);
            kn[..toCopy].CopyTo(output.Slice(offset, toCopy));
            offset += toCopy;
        }
    }

    /// <summary>
    /// Writes the shared secret K to the hash context using the specified encoding.
    /// </summary>
    private static void WriteSharedSecretToHash(IncrementalHash hash, ReadOnlySpan<byte> value, SharedSecretEncoding encoding)
    {
        if (encoding == SharedSecretEncoding.String)
            WriteStringToHash(hash, value);
        else
            WriteMpintToHash(hash, value);
    }

    /// <summary>
    /// Writes an mpint to the hash context.
    /// The shared secret from X25519 needs to be encoded as mpint for hashing.
    /// </summary>
    private static void WriteMpintToHash(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        // mpint: uint32 length followed by value in big-endian with leading zeros stripped
        // and a leading 0x00 if high bit is set

        // Find first non-zero byte
        var start = 0;
        while (start < value.Length && value[start] == 0)
            start++;

        if (start == value.Length)
        {
            // Value is zero
            Span<byte> zeroLen = stackalloc byte[4] { 0, 0, 0, 0 };
            hash.AppendData(zeroLen);
            return;
        }

        var needsLeadingZero = (value[start] & 0x80) != 0;
        var length = value.Length - start + (needsLeadingZero ? 1 : 0);

        Span<byte> lengthBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)length);
        hash.AppendData(lengthBytes);

        if (needsLeadingZero)
        {
            Span<byte> zero = stackalloc byte[1] { 0 };
            hash.AppendData(zero);
        }

        hash.AppendData(value[start..]);
    }

    /// <summary>
    /// Writes a string (4-byte length prefix + raw bytes) to the hash context.
    /// Used for mlkem768x25519-sha256 where K is encoded as string, not mpint.
    /// </summary>
    private static void WriteStringToHash(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)value.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(value);
    }

    /// <summary>
    /// Holds all derived keys for a direction (client-to-server or server-to-client).
    /// </summary>
    public readonly struct DirectionalKeys
    {
        public readonly byte[] Iv;
        public readonly byte[] EncryptionKey;
        public readonly byte[] IntegrityKey;

        public DirectionalKeys(byte[] iv, byte[] encryptionKey, byte[] integrityKey)
        {
            Iv = iv;
            EncryptionKey = encryptionKey;
            IntegrityKey = integrityKey;
        }
    }

    /// <summary>
    /// Derives all keys for both directions.
    /// </summary>
    /// <param name="sharedSecret">Shared secret K.</param>
    /// <param name="exchangeHash">Exchange hash H.</param>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ivSize">IV size in bytes.</param>
    /// <param name="keySize">Encryption key size in bytes.</param>
    /// <param name="integrityKeySize">Integrity key size in bytes.</param>
    /// <param name="encoding">How K is encoded in the hash (mpint or string).</param>
    /// <returns>Tuple of (client-to-server keys, server-to-client keys).</returns>
    public static (DirectionalKeys ClientToServer, DirectionalKeys ServerToClient) DeriveAllKeys(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> exchangeHash,
        ReadOnlySpan<byte> sessionId,
        int ivSize,
        int keySize,
        int integrityKeySize,
        SharedSecretEncoding encoding = SharedSecretEncoding.Mpint)
    {
        var clientToServer = new DirectionalKeys(
            DeriveKey(sharedSecret, exchangeHash, KeyId.IvClientToServer, sessionId, ivSize, encoding),
            DeriveKey(sharedSecret, exchangeHash, KeyId.EncryptionKeyClientToServer, sessionId, keySize, encoding),
            DeriveKey(sharedSecret, exchangeHash, KeyId.IntegrityKeyClientToServer, sessionId, integrityKeySize, encoding)
        );

        var serverToClient = new DirectionalKeys(
            DeriveKey(sharedSecret, exchangeHash, KeyId.IvServerToClient, sessionId, ivSize, encoding),
            DeriveKey(sharedSecret, exchangeHash, KeyId.EncryptionKeyServerToClient, sessionId, keySize, encoding),
            DeriveKey(sharedSecret, exchangeHash, KeyId.IntegrityKeyServerToClient, sessionId, integrityKeySize, encoding)
        );

        return (clientToServer, serverToClient);
    }
}
