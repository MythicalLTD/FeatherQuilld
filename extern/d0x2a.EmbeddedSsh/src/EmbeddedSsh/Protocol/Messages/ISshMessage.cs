namespace d0x2a.EmbeddedSsh.Protocol.Messages;

/// <summary>
/// Base interface for all SSH messages.
/// </summary>
public interface ISshMessage
{
    /// <summary>
    /// The message type byte.
    /// </summary>
    SshMessageType MessageType { get; }

    /// <summary>
    /// Writes the message payload (excluding message type byte) to the buffer.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    int WriteTo(Span<byte> buffer);

    /// <summary>
    /// Calculates the size of the message payload (excluding message type byte).
    /// </summary>
    int GetSize();
}
