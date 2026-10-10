using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Crypto;

namespace d0x2a.EmbeddedSsh.Transport.Algorithms;

/// <summary>
/// Hybrid post-quantum key exchange combining ML-KEM-768 with X25519
/// (mlkem768x25519-sha256, draft-ietf-sshm-mlkem-hybrid-kex).
/// </summary>
public sealed class MlKem768x25519Kex : IKexAlgorithm
{
    /// <summary>ML-KEM-768 encapsulation key size.</summary>
    private const int MlKemEkSize = 1184;

    /// <summary>ML-KEM-768 ciphertext size.</summary>
    private const int MlKemCtSize = 1088;

    /// <summary>X25519 public key size.</summary>
    private const int X25519PkSize = 32;

    /// <summary>C_INIT = ek (1184) || X25519 pk (32) = 1216 bytes.</summary>
    private const int ClientInitSize = MlKemEkSize + X25519PkSize;

    /// <summary>S_REPLY = ct (1088) || X25519 pk (32) = 1120 bytes.</summary>
    private const int ServerReplySize = MlKemCtSize + X25519PkSize;

    public string Name => "mlkem768x25519-sha256";

    public SharedSecretEncoding SharedSecretEncoding => SharedSecretEncoding.String;

    public KexExchangeResult ServerExchange(ReadOnlySpan<byte> clientEphemeral)
    {
        if (clientEphemeral.Length != ClientInitSize)
            throw new ArgumentException(
                $"C_INIT must be {ClientInitSize} bytes (got {clientEphemeral.Length})",
                nameof(clientEphemeral));

        // Split C_INIT into ML-KEM ek and X25519 pk
        var clientEk = clientEphemeral[..MlKemEkSize];
        var clientX25519Pk = clientEphemeral[MlKemEkSize..];

        // ML-KEM-768: import client's encapsulation key and encapsulate
        byte[] mlkemCiphertext;
        byte[] kPq;
        using (var mlkem = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, clientEk))
        {
            mlkem.Encapsulate(out mlkemCiphertext, out kPq);
        }

        // X25519: generate server keypair and compute shared secret
        var (serverX25519Private, serverX25519Public) = X25519.GenerateKeyPair();
        var kCl = X25519.ComputeSharedSecret(serverX25519Private, clientX25519Pk);

        // K = SHA-256(K_PQ || K_CL) — raw concatenation, not length-prefixed
        var sharedSecret = SHA256.HashData([.. kPq, .. kCl]);

        // S_REPLY = ciphertext (1088) || X25519 pk (32)
        var serverEphemeral = new byte[ServerReplySize];
        mlkemCiphertext.CopyTo(serverEphemeral.AsSpan());
        serverX25519Public.CopyTo(serverEphemeral.AsSpan(MlKemCtSize));

        return new KexExchangeResult(serverEphemeral, sharedSecret);
    }

    /// <summary>
    /// Computes the exchange hash H per draft-ietf-sshm-mlkem-hybrid-kex.
    ///
    /// H = hash(V_C || V_S || I_C || I_S || K_S || C_INIT || S_REPLY || K)
    ///
    /// K is encoded as string (not mpint).
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
        // Calculate total size
        var totalSize = 4 + clientVersion.Length +
                        4 + serverVersion.Length +
                        4 + clientKexInit.Length +
                        4 + serverKexInit.Length +
                        4 + hostKeyBlob.Length +
                        4 + clientEphemeral.Length +
                        4 + serverEphemeral.Length +
                        4 + sharedSecret.Length; // K as string (4-byte len + raw bytes)

        // Build the hash input
        var hashInput = totalSize <= 4096 ? stackalloc byte[totalSize] : new byte[totalSize];
        var offset = 0;

        offset += Curve25519Kex.WriteString(hashInput[offset..], clientVersion);
        offset += Curve25519Kex.WriteString(hashInput[offset..], serverVersion);
        offset += Curve25519Kex.WriteString(hashInput[offset..], clientKexInit);
        offset += Curve25519Kex.WriteString(hashInput[offset..], serverKexInit);
        offset += Curve25519Kex.WriteString(hashInput[offset..], hostKeyBlob);
        offset += Curve25519Kex.WriteString(hashInput[offset..], clientEphemeral);
        offset += Curve25519Kex.WriteString(hashInput[offset..], serverEphemeral);

        // K as string (not mpint) — 4-byte length prefix + raw bytes
        offset += Curve25519Kex.WriteString(hashInput[offset..], sharedSecret);

        return SHA256.HashData(hashInput[..offset]);
    }
}
