using System.Security.Cryptography;

namespace d0x2a.EmbeddedSsh.Protocol.Messages;

/// <summary>
/// SSH_MSG_DISCONNECT (RFC 4253 §11.1)
/// </summary>
public sealed class DisconnectMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.Disconnect;

    public required DisconnectReason Reason { get; init; }
    public required string Description { get; init; }
    public string LanguageTag { get; init; } = "";

    public static DisconnectMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new DisconnectMessage
        {
            Reason = (DisconnectReason)reader.ReadUInt32(),
            Description = reader.ReadString(),
            LanguageTag = reader.ReadString()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32((uint)Reason);
        writer.WriteString(Description);
        writer.WriteString(LanguageTag);
        return writer.Position;
    }

    public int GetSize() =>
        4 + SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Description)) +
        SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(LanguageTag)) - 8;
}

/// <summary>
/// SSH_MSG_IGNORE (RFC 4253 §11.2)
/// </summary>
public sealed class IgnoreMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.Ignore;

    public ReadOnlyMemory<byte> Data { get; init; }

    public static IgnoreMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new IgnoreMessage
        {
            Data = reader.ReadBinaryString().ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteBinaryString(Data.Span);
        return writer.Position;
    }

    public int GetSize() => SshWriter.GetBinaryStringSize(Data.Length);
}

/// <summary>
/// SSH_MSG_UNIMPLEMENTED (RFC 4253 §11.4)
/// </summary>
public sealed class UnimplementedMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.Unimplemented;

    public required uint SequenceNumber { get; init; }

    public static UnimplementedMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new UnimplementedMessage
        {
            SequenceNumber = reader.ReadUInt32()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(SequenceNumber);
        return writer.Position;
    }

    public int GetSize() => 4;
}

/// <summary>
/// SSH_MSG_DEBUG (RFC 4253 §11.3)
/// </summary>
public sealed class DebugMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.Debug;

    public required bool AlwaysDisplay { get; init; }
    public required string Message { get; init; }
    public string LanguageTag { get; init; } = "";

    public static DebugMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new DebugMessage
        {
            AlwaysDisplay = reader.ReadBoolean(),
            Message = reader.ReadString(),
            LanguageTag = reader.ReadString()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteBoolean(AlwaysDisplay);
        writer.WriteString(Message);
        writer.WriteString(LanguageTag);
        return writer.Position;
    }

    public int GetSize() =>
        1 + SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Message)) +
        SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(LanguageTag));
}

/// <summary>
/// SSH_MSG_SERVICE_REQUEST (RFC 4253 §10)
/// </summary>
public sealed class ServiceRequestMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ServiceRequest;

    public required string ServiceName { get; init; }

    public static ServiceRequestMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ServiceRequestMessage
        {
            ServiceName = reader.ReadString()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(ServiceName);
        return writer.Position;
    }

    public int GetSize() => SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(ServiceName));
}

/// <summary>
/// SSH_MSG_SERVICE_ACCEPT (RFC 4253 §10)
/// </summary>
public sealed class ServiceAcceptMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ServiceAccept;

    public required string ServiceName { get; init; }

    public static ServiceAcceptMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ServiceAcceptMessage
        {
            ServiceName = reader.ReadString()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(ServiceName);
        return writer.Position;
    }

    public int GetSize() => SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(ServiceName));
}

/// <summary>
/// SSH_MSG_NEWKEYS (RFC 4253 §7.3)
/// </summary>
public sealed class NewKeysMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.NewKeys;

    public static NewKeysMessage Instance { get; } = new();

    public static NewKeysMessage Parse(ReadOnlySpan<byte> payload) => Instance;

    public int WriteTo(Span<byte> buffer) => 0;

    public int GetSize() => 0;
}

/// <summary>
/// SSH_MSG_EXT_INFO (RFC 8308)
/// Used to advertise supported signature algorithms for public key authentication.
/// </summary>
public sealed class ExtInfoMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ExtInfo;

    /// <summary>
    /// Extensions as name-value pairs.
    /// </summary>
    public required IReadOnlyList<(string Name, string Value)> Extensions { get; init; }

    public static ExtInfoMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        var count = reader.ReadUInt32();
        var extensions = new List<(string, string)>((int)count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var value = reader.ReadString();
            extensions.Add((name, value));
        }

        return new ExtInfoMessage { Extensions = extensions };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32((uint)Extensions.Count);

        foreach (var (name, value) in Extensions)
        {
            writer.WriteString(name);
            writer.WriteString(value);
        }

        return writer.Position;
    }

    public int GetSize()
    {
        var size = 4; // extension count
        foreach (var (name, value) in Extensions)
        {
            size += SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(name));
            size += SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(value));
        }
        return size;
    }
}

/// <summary>
/// SSH_MSG_KEXINIT (RFC 4253 §7.1)
/// </summary>
public sealed class KexInitMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.KexInit;

    public byte[] Cookie { get; init; }
    public required IReadOnlyList<string> KexAlgorithms { get; init; }
    public required IReadOnlyList<string> HostKeyAlgorithms { get; init; }
    public required IReadOnlyList<string> EncryptionAlgorithmsClientToServer { get; init; }
    public required IReadOnlyList<string> EncryptionAlgorithmsServerToClient { get; init; }
    public required IReadOnlyList<string> MacAlgorithmsClientToServer { get; init; }
    public required IReadOnlyList<string> MacAlgorithmsServerToClient { get; init; }
    public required IReadOnlyList<string> CompressionAlgorithmsClientToServer { get; init; }
    public required IReadOnlyList<string> CompressionAlgorithmsServerToClient { get; init; }
    public IReadOnlyList<string> LanguagesClientToServer { get; init; } = [];
    public IReadOnlyList<string> LanguagesServerToClient { get; init; } = [];
    public bool FirstKexPacketFollows { get; init; }

    public KexInitMessage()
    {
        Cookie = new byte[16];
        RandomNumberGenerator.Fill(Cookie);
    }

    public static KexInitMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);

        var cookie = reader.ReadBytes(16).ToArray();

        return new KexInitMessage
        {
            Cookie = cookie,
            KexAlgorithms = reader.ReadNameList(),
            HostKeyAlgorithms = reader.ReadNameList(),
            EncryptionAlgorithmsClientToServer = reader.ReadNameList(),
            EncryptionAlgorithmsServerToClient = reader.ReadNameList(),
            MacAlgorithmsClientToServer = reader.ReadNameList(),
            MacAlgorithmsServerToClient = reader.ReadNameList(),
            CompressionAlgorithmsClientToServer = reader.ReadNameList(),
            CompressionAlgorithmsServerToClient = reader.ReadNameList(),
            LanguagesClientToServer = reader.ReadNameList(),
            LanguagesServerToClient = reader.ReadNameList(),
            FirstKexPacketFollows = reader.ReadBoolean()
            // reserved uint32 is ignored
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteBytes(Cookie);
        writer.WriteNameList(KexAlgorithms);
        writer.WriteNameList(HostKeyAlgorithms);
        writer.WriteNameList(EncryptionAlgorithmsClientToServer);
        writer.WriteNameList(EncryptionAlgorithmsServerToClient);
        writer.WriteNameList(MacAlgorithmsClientToServer);
        writer.WriteNameList(MacAlgorithmsServerToClient);
        writer.WriteNameList(CompressionAlgorithmsClientToServer);
        writer.WriteNameList(CompressionAlgorithmsServerToClient);
        writer.WriteNameList(LanguagesClientToServer);
        writer.WriteNameList(LanguagesServerToClient);
        writer.WriteBoolean(FirstKexPacketFollows);
        writer.WriteUInt32(0); // reserved
        return writer.Position;
    }

    public int GetSize() =>
        16 + // cookie
        SshWriter.GetNameListSize(KexAlgorithms) +
        SshWriter.GetNameListSize(HostKeyAlgorithms) +
        SshWriter.GetNameListSize(EncryptionAlgorithmsClientToServer) +
        SshWriter.GetNameListSize(EncryptionAlgorithmsServerToClient) +
        SshWriter.GetNameListSize(MacAlgorithmsClientToServer) +
        SshWriter.GetNameListSize(MacAlgorithmsServerToClient) +
        SshWriter.GetNameListSize(CompressionAlgorithmsClientToServer) +
        SshWriter.GetNameListSize(CompressionAlgorithmsServerToClient) +
        SshWriter.GetNameListSize(LanguagesClientToServer) +
        SshWriter.GetNameListSize(LanguagesServerToClient) +
        1 + // first_kex_packet_follows
        4;  // reserved

    /// <summary>
    /// Returns the full message bytes including message type for hash computation.
    /// </summary>
    public byte[] ToBytes()
    {
        var size = 1 + GetSize();
        var buffer = new byte[size];
        buffer[0] = (byte)MessageType;
        WriteTo(buffer.AsSpan(1));
        return buffer;
    }
}

/// <summary>
/// SSH_MSG_KEX_ECDH_INIT (RFC 5656)
/// </summary>
public sealed class KexEcdhInitMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.KexEcdhInit;

    /// <summary>
    /// Client's ephemeral public key (32 bytes for X25519).
    /// </summary>
    public required ReadOnlyMemory<byte> ClientPublicKey { get; init; }

    public static KexEcdhInitMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new KexEcdhInitMessage
        {
            ClientPublicKey = reader.ReadBinaryString().ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteBinaryString(ClientPublicKey.Span);
        return writer.Position;
    }

    public int GetSize() => SshWriter.GetBinaryStringSize(ClientPublicKey.Length);
}

/// <summary>
/// SSH_MSG_KEX_ECDH_REPLY (RFC 5656)
/// </summary>
public sealed class KexEcdhReplyMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.KexEcdhReply;

    /// <summary>
    /// Server's host public key blob.
    /// </summary>
    public required ReadOnlyMemory<byte> HostKeyBlob { get; init; }

    /// <summary>
    /// Server's ephemeral public key (32 bytes for X25519).
    /// </summary>
    public required ReadOnlyMemory<byte> ServerPublicKey { get; init; }

    /// <summary>
    /// Signature over the exchange hash.
    /// </summary>
    public required ReadOnlyMemory<byte> Signature { get; init; }

    public static KexEcdhReplyMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new KexEcdhReplyMessage
        {
            HostKeyBlob = reader.ReadBinaryString().ToArray(),
            ServerPublicKey = reader.ReadBinaryString().ToArray(),
            Signature = reader.ReadBinaryString().ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteBinaryString(HostKeyBlob.Span);
        writer.WriteBinaryString(ServerPublicKey.Span);
        writer.WriteBinaryString(Signature.Span);
        return writer.Position;
    }

    public int GetSize() =>
        SshWriter.GetBinaryStringSize(HostKeyBlob.Length) +
        SshWriter.GetBinaryStringSize(ServerPublicKey.Length) +
        SshWriter.GetBinaryStringSize(Signature.Length);
}
