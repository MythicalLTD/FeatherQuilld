using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Crypto;
using d0x2a.EmbeddedSsh.Protocol;

namespace d0x2a.EmbeddedSsh.HostKeys;

/// <summary>
/// Ed25519 host key implementation.
/// </summary>
public sealed class Ed25519HostKey : IHostKey
{
    private readonly byte[] _privateKey;
    private readonly byte[] _publicKey;

    public string Algorithm => "ssh-ed25519";

    /// <summary>
    /// Creates a new Ed25519 host key from existing key material.
    /// </summary>
    /// <param name="privateKey">32-byte private key.</param>
    public Ed25519HostKey(byte[] privateKey)
    {
        if (privateKey.Length != 32)
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));

        _privateKey = (byte[])privateKey.Clone();
        _publicKey = Ed25519.GetPublicKey(_privateKey);
    }

    /// <summary>
    /// Creates a new Ed25519 host key from an OpenSSH private key file.
    /// </summary>
    public static Ed25519HostKey FromOpenSshFile(string path)
    {
        var content = File.ReadAllText(path);
        return FromOpenSshString(content);
    }

    /// <summary>
    /// Creates a new Ed25519 host key from an OpenSSH private key string.
    /// </summary>
    public static Ed25519HostKey FromOpenSshString(string content)
    {
        // Parse OpenSSH format:
        // -----BEGIN OPENSSH PRIVATE KEY-----
        // base64 encoded data
        // -----END OPENSSH PRIVATE KEY-----

        var lines = content.Split('\n');
        var base64Lines = lines
            .Where(l => !l.StartsWith("-----") && !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim());
        var base64 = string.Join("", base64Lines);

        var data = Convert.FromBase64String(base64);
        return ParseOpenSshPrivateKey(data);
    }

    /// <summary>
    /// Generates a new random Ed25519 host key.
    /// </summary>
    public static Ed25519HostKey Generate()
    {
        var (privateKey, _) = Ed25519.GenerateKeyPair();
        return new Ed25519HostKey(privateKey);
    }

    public byte[] GetPublicKeyBlob()
    {
        // Format: string "ssh-ed25519" || string public_key (32 bytes)
        var blobSize = 4 + 11 + 4 + 32;  // ssh-ed25519 = 11 chars
        var blob = new byte[blobSize];
        var writer = new SshWriter(blob);
        writer.WriteString("ssh-ed25519");
        writer.WriteBinaryString(_publicKey);
        return blob;
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        // Create signature
        var signature = Ed25519.Sign(_privateKey, data);

        // Format: string "ssh-ed25519" || string signature
        var blobSize = 4 + 11 + 4 + 64;  // algorithm + signature
        var blob = new byte[blobSize];
        var writer = new SshWriter(blob);
        writer.WriteString("ssh-ed25519");
        writer.WriteBinaryString(signature);
        return blob;
    }

    /// <summary>
    /// Gets the private key bytes (for persistence).
    /// </summary>
    public byte[] GetPrivateKey()
    {
        return (byte[])_privateKey.Clone();
    }

    /// <summary>
    /// Gets the public key bytes.
    /// </summary>
    public byte[] GetPublicKey()
    {
        return (byte[])_publicKey.Clone();
    }

    private static Ed25519HostKey ParseOpenSshPrivateKey(byte[] data)
    {
        // OpenSSH private key format (simplified):
        // AUTH_MAGIC = "openssh-key-v1\0"
        // string ciphername ("none" for unencrypted)
        // string kdfname ("none" for unencrypted)
        // string kdfoptions (empty for "none")
        // uint32 number of keys (1)
        // string public key
        // string private key section (contains checkint, private key, comment, padding)

        var authMagic = "openssh-key-v1\0"u8;

        if (data.Length < authMagic.Length)
            throw new ArgumentException("Invalid OpenSSH private key format");

        if (!data.AsSpan(0, authMagic.Length).SequenceEqual(authMagic))
            throw new ArgumentException("Invalid OpenSSH private key magic");

        var reader = new SshReader(data.AsSpan(authMagic.Length));

        var cipherName = reader.ReadString();
        if (cipherName != "none")
            throw new NotSupportedException("Encrypted private keys are not supported");

        var kdfName = reader.ReadString();
        if (kdfName != "none")
            throw new NotSupportedException("Encrypted private keys are not supported");

        var kdfOptions = reader.ReadBinaryString();
        var numKeys = reader.ReadUInt32();
        if (numKeys != 1)
            throw new NotSupportedException("Multiple keys not supported");

        var publicKeyBlob = reader.ReadBinaryString();
        var privateKeySection = reader.ReadBinaryString();

        // Parse private key section
        var privReader = new SshReader(privateKeySection);

        var checkInt1 = privReader.ReadUInt32();
        var checkInt2 = privReader.ReadUInt32();
        if (checkInt1 != checkInt2)
            throw new ArgumentException("Private key checksum mismatch");

        var keyType = privReader.ReadString();
        if (keyType != "ssh-ed25519")
            throw new NotSupportedException($"Key type {keyType} not supported");

        // Ed25519 public key (32 bytes)
        var pubKey = privReader.ReadBinaryString();
        if (pubKey.Length != 32)
            throw new ArgumentException("Invalid Ed25519 public key length");

        // Ed25519 private key (64 bytes: 32 byte seed + 32 byte public key)
        var privKeyData = privReader.ReadBinaryString();
        if (privKeyData.Length != 64)
            throw new ArgumentException("Invalid Ed25519 private key length");

        // First 32 bytes are the private key seed
        var privateKey = privKeyData.Slice(0, 32).ToArray();

        // Verify public key matches
        var computedPublic = Ed25519.GetPublicKey(privateKey);
        if (!computedPublic.AsSpan().SequenceEqual(pubKey))
            throw new ArgumentException("Public key does not match private key");

        return new Ed25519HostKey(privateKey);
    }

    /// <summary>
    /// Exports the key in OpenSSH private key format.
    /// </summary>
    public string ExportOpenSshPrivateKey(string comment = "")
    {
        // Build the key data
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Auth magic
        bw.Write("openssh-key-v1\0"u8);

        // Cipher/KDF (unencrypted)
        WriteString(bw, "none");
        WriteString(bw, "none");
        WriteString(bw, "");  // empty kdf options

        // Number of keys
        WriteUInt32(bw, 1);

        // Public key blob
        var publicBlob = GetPublicKeyBlob();
        WriteBytes(bw, publicBlob);

        // Private key section
        using var privMs = new MemoryStream();
        using var privBw = new BinaryWriter(privMs);

        // Random check integers
        var checkInt = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        WriteUInt32(privBw, checkInt);
        WriteUInt32(privBw, checkInt);

        // Key type
        WriteString(privBw, "ssh-ed25519");

        // Public key
        WriteBytes(privBw, _publicKey);

        // Private key (seed + public)
        var fullPrivate = new byte[64];
        _privateKey.CopyTo(fullPrivate.AsSpan(0));
        _publicKey.CopyTo(fullPrivate.AsSpan(32));
        WriteBytes(privBw, fullPrivate);

        // Comment
        WriteString(privBw, comment);

        // Padding
        var privData = privMs.ToArray();
        var padLen = 8 - (privData.Length % 8);
        if (padLen == 8) padLen = 0;
        for (int i = 1; i <= padLen; i++)
            privBw.Write((byte)i);

        privData = privMs.ToArray();
        WriteBytes(bw, privData);

        var data = ms.ToArray();
        var base64 = Convert.ToBase64String(data);

        // Format as PEM
        var lines = new List<string> { "-----BEGIN OPENSSH PRIVATE KEY-----" };
        for (int i = 0; i < base64.Length; i += 70)
        {
            lines.Add(base64.Substring(i, Math.Min(70, base64.Length - i)));
        }
        lines.Add("-----END OPENSSH PRIVATE KEY-----");

        return string.Join("\n", lines);
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        WriteBytes(bw, bytes);
    }

    private static void WriteBytes(BinaryWriter bw, ReadOnlySpan<byte> data)
    {
        WriteUInt32(bw, (uint)data.Length);
        bw.Write(data);
    }

    private static void WriteUInt32(BinaryWriter bw, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        bw.Write(buf);
    }
}
