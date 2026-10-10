namespace d0x2a.EmbeddedSsh.Protocol.Messages;

/// <summary>
/// SSH_MSG_GLOBAL_REQUEST (RFC 4254 §4)
/// </summary>
public sealed class GlobalRequestMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.GlobalRequest;

    public required string RequestName { get; init; }
    public required bool WantReply { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    public static GlobalRequestMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        var name = reader.ReadString();
        var wantReply = reader.ReadBoolean();
        var data = reader.RemainingSpan.ToArray();

        return new GlobalRequestMessage
        {
            RequestName = name,
            WantReply = wantReply,
            Data = data
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(RequestName);
        writer.WriteBoolean(WantReply);
        writer.WriteBytes(Data.Span);
        return writer.Position;
    }

    public int GetSize() =>
        SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(RequestName)) +
        1 + Data.Length;
}

/// <summary>
/// SSH_MSG_REQUEST_SUCCESS (RFC 4254 §4)
/// </summary>
public sealed class RequestSuccessMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.RequestSuccess;

    public ReadOnlyMemory<byte> Data { get; init; }

    public static RequestSuccessMessage Parse(ReadOnlySpan<byte> payload)
    {
        return new RequestSuccessMessage
        {
            Data = payload.ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        Data.Span.CopyTo(buffer);
        return Data.Length;
    }

    public int GetSize() => Data.Length;
}

/// <summary>
/// SSH_MSG_REQUEST_FAILURE (RFC 4254 §4)
/// </summary>
public sealed class RequestFailureMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.RequestFailure;

    public static RequestFailureMessage Instance { get; } = new();

    public static RequestFailureMessage Parse(ReadOnlySpan<byte> payload) => Instance;

    public int WriteTo(Span<byte> buffer) => 0;

    public int GetSize() => 0;
}

/// <summary>
/// SSH_MSG_CHANNEL_OPEN (RFC 4254 §5.1)
/// </summary>
public sealed class ChannelOpenMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelOpen;

    public required string ChannelType { get; init; }
    public required uint SenderChannel { get; init; }
    public required uint InitialWindowSize { get; init; }
    public required uint MaximumPacketSize { get; init; }
    public ReadOnlyMemory<byte> ChannelSpecificData { get; init; }

    public static ChannelOpenMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelOpenMessage
        {
            ChannelType = reader.ReadString(),
            SenderChannel = reader.ReadUInt32(),
            InitialWindowSize = reader.ReadUInt32(),
            MaximumPacketSize = reader.ReadUInt32(),
            ChannelSpecificData = reader.RemainingSpan.ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteString(ChannelType);
        writer.WriteUInt32(SenderChannel);
        writer.WriteUInt32(InitialWindowSize);
        writer.WriteUInt32(MaximumPacketSize);
        writer.WriteBytes(ChannelSpecificData.Span);
        return writer.Position;
    }

    public int GetSize() =>
        SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(ChannelType)) +
        4 + 4 + 4 + ChannelSpecificData.Length;
}

/// <summary>
/// SSH_MSG_CHANNEL_OPEN_CONFIRMATION (RFC 4254 §5.1)
/// </summary>
public sealed class ChannelOpenConfirmationMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelOpenConfirmation;

    public required uint RecipientChannel { get; init; }
    public required uint SenderChannel { get; init; }
    public required uint InitialWindowSize { get; init; }
    public required uint MaximumPacketSize { get; init; }
    public ReadOnlyMemory<byte> ChannelSpecificData { get; init; }

    public static ChannelOpenConfirmationMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelOpenConfirmationMessage
        {
            RecipientChannel = reader.ReadUInt32(),
            SenderChannel = reader.ReadUInt32(),
            InitialWindowSize = reader.ReadUInt32(),
            MaximumPacketSize = reader.ReadUInt32(),
            ChannelSpecificData = reader.RemainingSpan.ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        writer.WriteUInt32(SenderChannel);
        writer.WriteUInt32(InitialWindowSize);
        writer.WriteUInt32(MaximumPacketSize);
        writer.WriteBytes(ChannelSpecificData.Span);
        return writer.Position;
    }

    public int GetSize() => 4 + 4 + 4 + 4 + ChannelSpecificData.Length;
}

/// <summary>
/// SSH_MSG_CHANNEL_OPEN_FAILURE (RFC 4254 §5.1)
/// </summary>
public sealed class ChannelOpenFailureMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelOpenFailure;

    public required uint RecipientChannel { get; init; }
    public required ChannelOpenFailureReason ReasonCode { get; init; }
    public required string Description { get; init; }
    public string LanguageTag { get; init; } = "";

    public static ChannelOpenFailureMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelOpenFailureMessage
        {
            RecipientChannel = reader.ReadUInt32(),
            ReasonCode = (ChannelOpenFailureReason)reader.ReadUInt32(),
            Description = reader.ReadString(),
            LanguageTag = reader.ReadString()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        writer.WriteUInt32((uint)ReasonCode);
        writer.WriteString(Description);
        writer.WriteString(LanguageTag);
        return writer.Position;
    }

    public int GetSize() =>
        4 + 4 +
        SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Description)) +
        SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(LanguageTag));
}

/// <summary>
/// SSH_MSG_CHANNEL_WINDOW_ADJUST (RFC 4254 §5.2)
/// </summary>
public sealed class ChannelWindowAdjustMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelWindowAdjust;

    public required uint RecipientChannel { get; init; }
    public required uint BytesToAdd { get; init; }

    public static ChannelWindowAdjustMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelWindowAdjustMessage
        {
            RecipientChannel = reader.ReadUInt32(),
            BytesToAdd = reader.ReadUInt32()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        writer.WriteUInt32(BytesToAdd);
        return writer.Position;
    }

    public int GetSize() => 8;
}

/// <summary>
/// SSH_MSG_CHANNEL_DATA (RFC 4254 §5.2)
/// </summary>
public sealed class ChannelDataMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelData;

    public required uint RecipientChannel { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }

    public static ChannelDataMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelDataMessage
        {
            RecipientChannel = reader.ReadUInt32(),
            Data = reader.ReadBinaryString().ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        writer.WriteBinaryString(Data.Span);
        return writer.Position;
    }

    public int GetSize() => 4 + SshWriter.GetBinaryStringSize(Data.Length);
}

/// <summary>
/// SSH_MSG_CHANNEL_EXTENDED_DATA (RFC 4254 §5.2)
/// </summary>
public sealed class ChannelExtendedDataMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelExtendedData;

    public required uint RecipientChannel { get; init; }
    public required ExtendedDataType DataTypeCode { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }

    public static ChannelExtendedDataMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelExtendedDataMessage
        {
            RecipientChannel = reader.ReadUInt32(),
            DataTypeCode = (ExtendedDataType)reader.ReadUInt32(),
            Data = reader.ReadBinaryString().ToArray()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        writer.WriteUInt32((uint)DataTypeCode);
        writer.WriteBinaryString(Data.Span);
        return writer.Position;
    }

    public int GetSize() => 4 + 4 + SshWriter.GetBinaryStringSize(Data.Length);
}

/// <summary>
/// SSH_MSG_CHANNEL_EOF (RFC 4254 §5.3)
/// </summary>
public sealed class ChannelEofMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelEof;

    public required uint RecipientChannel { get; init; }

    public static ChannelEofMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelEofMessage
        {
            RecipientChannel = reader.ReadUInt32()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        return writer.Position;
    }

    public int GetSize() => 4;
}

/// <summary>
/// SSH_MSG_CHANNEL_CLOSE (RFC 4254 §5.3)
/// </summary>
public sealed class ChannelCloseMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelClose;

    public required uint RecipientChannel { get; init; }

    public static ChannelCloseMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelCloseMessage
        {
            RecipientChannel = reader.ReadUInt32()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        return writer.Position;
    }

    public int GetSize() => 4;
}

/// <summary>
/// SSH_MSG_CHANNEL_REQUEST (RFC 4254 §5.4)
/// </summary>
public sealed class ChannelRequestMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelRequest;

    public required uint RecipientChannel { get; init; }
    public required string RequestType { get; init; }
    public required bool WantReply { get; init; }

    // Request-specific data
    public ReadOnlyMemory<byte> RequestData { get; init; }

    // Parsed fields for known request types

    /// <summary>For "pty-req": TERM environment variable.</summary>
    public string? Term { get; init; }

    /// <summary>For "pty-req": terminal width in characters.</summary>
    public uint TerminalWidthChars { get; init; }

    /// <summary>For "pty-req": terminal height in rows.</summary>
    public uint TerminalHeightRows { get; init; }

    /// <summary>For "pty-req": terminal width in pixels.</summary>
    public uint TerminalWidthPixels { get; init; }

    /// <summary>For "pty-req": terminal height in pixels.</summary>
    public uint TerminalHeightPixels { get; init; }

    /// <summary>For "pty-req": encoded terminal modes.</summary>
    public ReadOnlyMemory<byte> TerminalModes { get; init; }

    /// <summary>For "exec": command to execute.</summary>
    public string? Command { get; init; }

    /// <summary>For "env": variable name.</summary>
    public string? EnvName { get; init; }

    /// <summary>For "env": variable value.</summary>
    public string? EnvValue { get; init; }

    /// <summary>For "signal": signal name.</summary>
    public string? SignalName { get; init; }

    /// <summary>For "exit-status": exit code.</summary>
    public uint? ExitStatus { get; init; }

    /// <summary>For "exit-signal": signal name.</summary>
    public string? ExitSignalName { get; init; }

    /// <summary>For "exit-signal": core dumped flag.</summary>
    public bool CoreDumped { get; init; }

    /// <summary>For "exit-signal": error message.</summary>
    public string? ErrorMessage { get; init; }

    public static ChannelRequestMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        var recipientChannel = reader.ReadUInt32();
        var requestType = reader.ReadString();
        var wantReply = reader.ReadBoolean();

        var requestData = reader.RemainingSpan.ToArray();

        // Default values
        string? term = null;
        uint terminalWidthChars = 0;
        uint terminalHeightRows = 0;
        uint terminalWidthPixels = 0;
        uint terminalHeightPixels = 0;
        ReadOnlyMemory<byte> terminalModes = default;
        string? command = null;
        string? envName = null;
        string? envValue = null;
        string? signalName = null;
        uint? exitStatus = null;
        string? exitSignalName = null;
        bool coreDumped = false;
        string? errorMessage = null;

        // Parse known request types
        switch (requestType)
        {
            case "pty-req":
                term = reader.ReadString();
                terminalWidthChars = reader.ReadUInt32();
                terminalHeightRows = reader.ReadUInt32();
                terminalWidthPixels = reader.ReadUInt32();
                terminalHeightPixels = reader.ReadUInt32();
                terminalModes = reader.ReadBinaryString().ToArray();
                break;

            case "exec":
                command = reader.ReadString();
                break;

            case "shell":
                // No additional data
                break;

            case "env":
                envName = reader.ReadString();
                envValue = reader.ReadString();
                break;

            case "signal":
                signalName = reader.ReadString();
                break;

            case "exit-status":
                exitStatus = reader.ReadUInt32();
                break;

            case "exit-signal":
                exitSignalName = reader.ReadString();
                coreDumped = reader.ReadBoolean();
                errorMessage = reader.ReadString();
                // language tag ignored
                break;

            case "window-change":
                terminalWidthChars = reader.ReadUInt32();
                terminalHeightRows = reader.ReadUInt32();
                terminalWidthPixels = reader.ReadUInt32();
                terminalHeightPixels = reader.ReadUInt32();
                break;
        }

        return new ChannelRequestMessage
        {
            RecipientChannel = recipientChannel,
            RequestType = requestType,
            WantReply = wantReply,
            RequestData = requestData,
            Term = term,
            TerminalWidthChars = terminalWidthChars,
            TerminalHeightRows = terminalHeightRows,
            TerminalWidthPixels = terminalWidthPixels,
            TerminalHeightPixels = terminalHeightPixels,
            TerminalModes = terminalModes,
            Command = command,
            EnvName = envName,
            EnvValue = envValue,
            SignalName = signalName,
            ExitStatus = exitStatus,
            ExitSignalName = exitSignalName,
            CoreDumped = coreDumped,
            ErrorMessage = errorMessage
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        writer.WriteString(RequestType);
        writer.WriteBoolean(WantReply);

        switch (RequestType)
        {
            case "pty-req":
                writer.WriteString(Term!);
                writer.WriteUInt32(TerminalWidthChars);
                writer.WriteUInt32(TerminalHeightRows);
                writer.WriteUInt32(TerminalWidthPixels);
                writer.WriteUInt32(TerminalHeightPixels);
                writer.WriteBinaryString(TerminalModes.Span);
                break;

            case "exec":
                writer.WriteString(Command!);
                break;

            case "shell":
                break;

            case "env":
                writer.WriteString(EnvName!);
                writer.WriteString(EnvValue!);
                break;

            case "signal":
                writer.WriteString(SignalName!);
                break;

            case "exit-status":
                writer.WriteUInt32(ExitStatus!.Value);
                break;

            case "exit-signal":
                writer.WriteString(ExitSignalName!);
                writer.WriteBoolean(CoreDumped);
                writer.WriteString(ErrorMessage ?? "");
                writer.WriteString(""); // language tag
                break;

            case "window-change":
                writer.WriteUInt32(TerminalWidthChars);
                writer.WriteUInt32(TerminalHeightRows);
                writer.WriteUInt32(TerminalWidthPixels);
                writer.WriteUInt32(TerminalHeightPixels);
                break;

            default:
                writer.WriteBytes(RequestData.Span);
                break;
        }

        return writer.Position;
    }

    public int GetSize()
    {
        var size = 4 + // recipient channel
                   SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(RequestType)) +
                   1; // want reply

        switch (RequestType)
        {
            case "pty-req":
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Term!));
                size += 4 + 4 + 4 + 4; // dimensions
                size += SshWriter.GetBinaryStringSize(TerminalModes.Length);
                break;

            case "exec":
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(Command!));
                break;

            case "env":
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(EnvName!));
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(EnvValue!));
                break;

            case "signal":
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(SignalName!));
                break;

            case "exit-status":
                size += 4;
                break;

            case "exit-signal":
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.ASCII.GetByteCount(ExitSignalName!));
                size += 1; // core dumped
                size += SshWriter.GetBinaryStringSize(System.Text.Encoding.UTF8.GetByteCount(ErrorMessage ?? ""));
                size += SshWriter.GetBinaryStringSize(0); // language tag
                break;

            case "window-change":
                size += 16;
                break;

            default:
                size += RequestData.Length;
                break;
        }

        return size;
    }

    /// <summary>
    /// Creates an exit-status request.
    /// </summary>
    public static ChannelRequestMessage CreateExitStatus(uint recipientChannel, uint exitStatus)
    {
        return new ChannelRequestMessage
        {
            RecipientChannel = recipientChannel,
            RequestType = "exit-status",
            WantReply = false,
            ExitStatus = exitStatus
        };
    }

    /// <summary>
    /// Creates an exit-signal request.
    /// </summary>
    public static ChannelRequestMessage CreateExitSignal(
        uint recipientChannel,
        string signalName,
        bool coreDumped,
        string errorMessage)
    {
        return new ChannelRequestMessage
        {
            RecipientChannel = recipientChannel,
            RequestType = "exit-signal",
            WantReply = false,
            ExitSignalName = signalName,
            CoreDumped = coreDumped,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// SSH_MSG_CHANNEL_SUCCESS (RFC 4254 §5.4)
/// </summary>
public sealed class ChannelSuccessMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelSuccess;

    public required uint RecipientChannel { get; init; }

    public static ChannelSuccessMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelSuccessMessage
        {
            RecipientChannel = reader.ReadUInt32()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        return writer.Position;
    }

    public int GetSize() => 4;
}

/// <summary>
/// SSH_MSG_CHANNEL_FAILURE (RFC 4254 §5.4)
/// </summary>
public sealed class ChannelFailureMessage : ISshMessage
{
    public SshMessageType MessageType => SshMessageType.ChannelFailure;

    public required uint RecipientChannel { get; init; }

    public static ChannelFailureMessage Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new SshReader(payload);
        return new ChannelFailureMessage
        {
            RecipientChannel = reader.ReadUInt32()
        };
    }

    public int WriteTo(Span<byte> buffer)
    {
        var writer = new SshWriter(buffer);
        writer.WriteUInt32(RecipientChannel);
        return writer.Position;
    }

    public int GetSize() => 4;
}
