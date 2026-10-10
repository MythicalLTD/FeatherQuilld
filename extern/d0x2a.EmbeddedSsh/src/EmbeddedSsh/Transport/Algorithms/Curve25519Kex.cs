using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Crypto;
using d0x2a.EmbeddedSsh.Protocol;

namespace d0x2a.EmbeddedSsh.Transport.Algorithms;

/// <summary>
/// Curve25519 key exchange using SHA-256 (curve25519-sha256 / curve25519-sha256@libssh.org).
/// RFC 8731: Key Exchange Method Using Curve25519 and Curve448.
/// </summary>
public sealed class Curve25519Kex : IKexAlgorithm
{
    /// <summary>
    /// Standard algorithm name.
    /// </summary>
    public string Name => "curve25519-sha256";

    /// <summary>
    /// Alternative algorithm name (libssh variant).
    /// </summary>
    public const string AlternativeName = "curve25519-sha256@libssh.org";

    public SharedSecretEncoding SharedSecretEncoding => SharedSecretEncoding.Mpint;

    public KexExchangeResult ServerExchange(ReadOnlySpan<byte> clientEphemeral)
    {
        if (clientEphemeral.Length != X25519.KeySize)
            throw new ArgumentException($"Client ephemeral key must be {X25519.KeySize} bytes", nameof(clientEphemeral));

        var (serverPrivate, serverPublic) = X25519.GenerateKeyPair();
        var sharedSecret = X25519.ComputeSharedSecret(serverPrivate, clientEphemeral);
        return new KexExchangeResult(serverPublic, sharedSecret);
    }

    /// <summary>
    /// Computes the exchange hash H per RFC 8731 Section 3.
    ///
    /// H = hash(V_C || V_S || I_C || I_S || K_S || Q_C || Q_S || K)
    ///
    /// Where:
    /// - V_C: client's identification string (SSH-2.0-...)
    /// - V_S: server's identification string (SSH-2.0-...)
    /// - I_C: client's SSH_MSG_KEXINIT
    /// - I_S: server's SSH_MSG_KEXINIT
    /// - K_S: server's public host key
    /// - Q_C: client's ephemeral public key
    /// - Q_S: server's ephemeral public key
    /// - K: shared secret (as mpint)
    /// </summary>
    public byte[] ComputeExchangeHash(
        ReadOnlySpan<byte> clientVersion,
        ReadOnlySpan<byte> serverVersion,
        ReadOnlySpan<byte> clientKexInit,
        ReadOnlySpan<byte> serverKexInit,
        ReadOnlySpan<byte> hostKeyBlob,
        ReadOnlySpan<byte> clientEphemeral,
        ReadOnlySpan<byte> serverEphemeral,
        ReadOnlySpan<byte> sharedSecret)
    {
        // Calculate total size for allocation
        var totalSize = CalculateHashInputSize(
            clientVersion, serverVersion,
            clientKexInit, serverKexInit,
            hostKeyBlob,
            clientEphemeral, serverEphemeral,
            sharedSecret);

        // Build the hash input
        Span<byte> hashInput = totalSize <= 2048 ? stackalloc byte[totalSize] : new byte[totalSize];
        var offset = 0;

        // V_C (string)
        offset += WriteString(hashInput[offset..], clientVersion);

        // V_S (string)
        offset += WriteString(hashInput[offset..], serverVersion);

        // I_C (string - the whole KEXINIT packet including message type)
        offset += WriteString(hashInput[offset..], clientKexInit);

        // I_S (string - the whole KEXINIT packet including message type)
        offset += WriteString(hashInput[offset..], serverKexInit);

        // K_S (string - host key blob)
        offset += WriteString(hashInput[offset..], hostKeyBlob);

        // Q_C (string - client's ephemeral public key, 32 bytes)
        offset += WriteString(hashInput[offset..], clientEphemeral);

        // Q_S (string - server's ephemeral public key, 32 bytes)
        offset += WriteString(hashInput[offset..], serverEphemeral);

        // K (mpint - shared secret)
        offset += WriteMpint(hashInput[offset..], sharedSecret);

        // Compute SHA-256 hash
        return SHA256.HashData(hashInput[..offset]);
    }

    private static int CalculateHashInputSize(
        ReadOnlySpan<byte> clientVersion,
        ReadOnlySpan<byte> serverVersion,
        ReadOnlySpan<byte> clientKexInit,
        ReadOnlySpan<byte> serverKexInit,
        ReadOnlySpan<byte> hostKeyBlob,
        ReadOnlySpan<byte> clientEphemeral,
        ReadOnlySpan<byte> serverEphemeral,
        ReadOnlySpan<byte> sharedSecret)
    {
        return 4 + clientVersion.Length +     // V_C
               4 + serverVersion.Length +     // V_S
               4 + clientKexInit.Length +     // I_C
               4 + serverKexInit.Length +     // I_S
               4 + hostKeyBlob.Length +       // K_S
               4 + clientEphemeral.Length +   // Q_C
               4 + serverEphemeral.Length +   // Q_S
               GetMpintSize(sharedSecret);    // K
    }

    /// <summary>
    /// Writes a string (length-prefixed bytes) to the buffer.
    /// </summary>
    internal static int WriteString(Span<byte> buffer, ReadOnlySpan<byte> value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value.Length);
        value.CopyTo(buffer[4..]);
        return 4 + value.Length;
    }

    /// <summary>
    /// Writes an mpint to the buffer.
    /// </summary>
    internal static int WriteMpint(Span<byte> buffer, ReadOnlySpan<byte> value)
    {
        // Strip leading zeros
        var start = 0;
        while (start < value.Length && value[start] == 0)
            start++;

        if (start == value.Length)
        {
            // Value is zero
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, 0);
            return 4;
        }

        var needsLeadingZero = (value[start] & 0x80) != 0;
        var length = value.Length - start + (needsLeadingZero ? 1 : 0);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)length);

        if (needsLeadingZero)
        {
            buffer[4] = 0;
            value[start..].CopyTo(buffer[5..]);
        }
        else
        {
            value[start..].CopyTo(buffer[4..]);
        }

        return 4 + length;
    }

    /// <summary>
    /// Gets the wire size of an mpint.
    /// </summary>
    internal static int GetMpintSize(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] == 0)
            start++;

        if (start == value.Length)
            return 4; // Zero: just length field

        var needsLeadingZero = (value[start] & 0x80) != 0;
        return 4 + value.Length - start + (needsLeadingZero ? 1 : 0);
    }
}
