using d0x2a.EmbeddedSsh.Protocol.Messages;

namespace d0x2a.EmbeddedSsh.Protocol;

/// <summary>
/// Parses SSH messages from binary payloads.
/// </summary>
public static class MessageParser
{
    /// <summary>
    /// Parses a message from a payload buffer.
    /// The first byte is the message type.
    /// </summary>
    public static ISshMessage Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            throw new SshProtocolException("Empty message payload");

        var messageType = (SshMessageType)payload[0];
        var data = payload[1..];

        return messageType switch
        {
            // Transport layer
            SshMessageType.Disconnect => DisconnectMessage.Parse(data),
            SshMessageType.Ignore => IgnoreMessage.Parse(data),
            SshMessageType.Unimplemented => UnimplementedMessage.Parse(data),
            SshMessageType.Debug => DebugMessage.Parse(data),
            SshMessageType.ServiceRequest => ServiceRequestMessage.Parse(data),
            SshMessageType.ServiceAccept => ServiceAcceptMessage.Parse(data),
            SshMessageType.ExtInfo => ExtInfoMessage.Parse(data),
            SshMessageType.KexInit => KexInitMessage.Parse(data),
            SshMessageType.NewKeys => NewKeysMessage.Parse(data),
            SshMessageType.KexEcdhInit => KexEcdhInitMessage.Parse(data),
            SshMessageType.KexEcdhReply => KexEcdhReplyMessage.Parse(data),

            // Authentication
            SshMessageType.UserauthRequest => UserauthRequestMessage.Parse(data),
            SshMessageType.UserauthFailure => UserauthFailureMessage.Parse(data),
            SshMessageType.UserauthSuccess => UserauthSuccessMessage.Parse(data),
            SshMessageType.UserauthBanner => UserauthBannerMessage.Parse(data),
            SshMessageType.UserauthPkOk => UserauthPkOkMessage.Parse(data),

            // Connection
            SshMessageType.GlobalRequest => GlobalRequestMessage.Parse(data),
            SshMessageType.RequestSuccess => RequestSuccessMessage.Parse(data),
            SshMessageType.RequestFailure => RequestFailureMessage.Parse(data),
            SshMessageType.ChannelOpen => ChannelOpenMessage.Parse(data),
            SshMessageType.ChannelOpenConfirmation => ChannelOpenConfirmationMessage.Parse(data),
            SshMessageType.ChannelOpenFailure => ChannelOpenFailureMessage.Parse(data),
            SshMessageType.ChannelWindowAdjust => ChannelWindowAdjustMessage.Parse(data),
            SshMessageType.ChannelData => ChannelDataMessage.Parse(data),
            SshMessageType.ChannelExtendedData => ChannelExtendedDataMessage.Parse(data),
            SshMessageType.ChannelEof => ChannelEofMessage.Parse(data),
            SshMessageType.ChannelClose => ChannelCloseMessage.Parse(data),
            SshMessageType.ChannelRequest => ChannelRequestMessage.Parse(data),
            SshMessageType.ChannelSuccess => ChannelSuccessMessage.Parse(data),
            SshMessageType.ChannelFailure => ChannelFailureMessage.Parse(data),

            _ => throw new SshProtocolException($"Unknown message type: {messageType}")
        };
    }

    /// <summary>
    /// Writes a message to a buffer, including the message type byte.
    /// </summary>
    /// <returns>Total bytes written including message type.</returns>
    public static int Write(ISshMessage message, Span<byte> buffer)
    {
        buffer[0] = (byte)message.MessageType;
        var payloadSize = message.WriteTo(buffer[1..]);
        return 1 + payloadSize;
    }

    /// <summary>
    /// Gets the total size of a message including the message type byte.
    /// </summary>
    public static int GetTotalSize(ISshMessage message)
    {
        return 1 + message.GetSize();
    }
}
