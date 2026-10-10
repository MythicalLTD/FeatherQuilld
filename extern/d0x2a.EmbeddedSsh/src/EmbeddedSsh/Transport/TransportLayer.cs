using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using d0x2a.EmbeddedSsh.Protocol;
using d0x2a.EmbeddedSsh.Protocol.Messages;
using d0x2a.EmbeddedSsh.Transport.Algorithms;

namespace d0x2a.EmbeddedSsh.Transport;

/// <summary>
/// SSH transport layer handling version exchange, packet framing, and encryption.
/// </summary>
public sealed class TransportLayer : IAsyncDisposable
{
    // RFC 4253 limits
    private const int MaxVersionLineLength = 255;
    private const int MaxPacketLength = 35000;
    private const int MinPacketLength = 16;
    private const int MinPaddingLength = 4;
    private const int MaxPaddingLength = 255;

    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Version strings
    private byte[] _serverVersion = null!;
    private byte[] _clientVersion = null!;

    // Packet sequence numbers
    private uint _sendSequence;
    private uint _receiveSequence;

    // Encryption state
    private ISshCipher _sendCipher = NullCipher.Instance;
    private ISshCipher _receiveCipher = NullCipher.Instance;

    // Session state
    private byte[] _sessionId = null!;
    private bool _isDisposed;

    // Buffers
    private readonly byte[] _sendBuffer = new byte[MaxPacketLength + 64]; // Extra space for AEAD tag
    private readonly byte[] _receiveBuffer = new byte[MaxPacketLength + 64];

    public TransportLayer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>
    /// Gets the session identifier (first exchange hash).
    /// </summary>
    public ReadOnlySpan<byte> SessionId => _sessionId;

    /// <summary>
    /// Gets whether encryption is active.
    /// </summary>
    public bool IsEncrypted => _sendCipher != NullCipher.Instance;

    #region Version Exchange

    /// <summary>
    /// Performs the SSH version exchange as the server.
    /// </summary>
    /// <param name="serverVersionString">Server version string (e.g., "SSH-2.0-EmbeddedSsh_1.0").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client's version string.</returns>
    public async ValueTask<string> ExchangeVersionsAsync(
        string serverVersionString,
        CancellationToken cancellationToken = default)
    {
        // Validate server version
        if (!serverVersionString.StartsWith("SSH-2.0-"))
            throw new ArgumentException("Server version must start with SSH-2.0-", nameof(serverVersionString));

        _serverVersion = Encoding.ASCII.GetBytes(serverVersionString);

        // Send server version
        var versionLine = Encoding.ASCII.GetBytes(serverVersionString + "\r\n");
        await _stream.WriteAsync(versionLine, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Read client version
        _clientVersion = await ReadVersionLineAsync(cancellationToken).ConfigureAwait(false);
        var clientVersionString = Encoding.ASCII.GetString(_clientVersion);

        // Validate client version
        if (!clientVersionString.StartsWith("SSH-2.0-") && !clientVersionString.StartsWith("SSH-1.99-"))
            throw new SshProtocolException(DisconnectReason.ProtocolVersionNotSupported,
                "Client version must be SSH-2.0 or SSH-1.99");

        return clientVersionString;
    }

    private async ValueTask<byte[]> ReadVersionLineAsync(CancellationToken cancellationToken)
    {
        var lineBuffer = new List<byte>();

        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            try
            {
                // Look for \r\n or \n
                var position = buffer.Start;
                while (buffer.TryGet(ref position, out var memory))
                {
                    for (var i = 0; i < memory.Length; i++)
                    {
                        var b = memory.Span[i];

                        if (b == '\n')
                        {
                            // Found end of line
                            // Remove trailing \r if present
                            if (lineBuffer.Count > 0 && lineBuffer[^1] == '\r')
                                lineBuffer.RemoveAt(lineBuffer.Count - 1);

                            var line = lineBuffer.ToArray();

                            // If line starts with SSH-, it's the version line
                            // Otherwise skip (could be banner lines)
                            if (line.Length >= 4 &&
                                line[0] == 'S' && line[1] == 'S' && line[2] == 'H' && line[3] == '-')
                            {
                                // Consume up to and including the newline
                                var consumedPosition = buffer.GetPosition(i + 1, buffer.Start);
                                foreach (var seg in buffer.Slice(buffer.Start, consumedPosition))
                                {
                                    // Just iterate to advance
                                }

                                // Calculate actual consumed position
                                var consumed = 0L;
                                foreach (var seg in buffer)
                                {
                                    for (var j = 0; j < seg.Length; j++)
                                    {
                                        consumed++;
                                        if (seg.Span[j] == '\n' && consumed >= lineBuffer.Count + 1)
                                        {
                                            _reader.AdvanceTo(buffer.GetPosition(consumed));
                                            return line;
                                        }
                                    }
                                }

                                // Fallback: consume whole buffer
                                _reader.AdvanceTo(buffer.End);
                                return line;
                            }

                            // Not a version line, clear and continue
                            lineBuffer.Clear();
                            continue;
                        }

                        lineBuffer.Add(b);

                        if (lineBuffer.Count > MaxVersionLineLength)
                            throw new SshProtocolException(DisconnectReason.ProtocolError,
                                "Version line too long");
                    }
                }

                // Need more data
                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
            catch
            {
                _reader.AdvanceTo(buffer.End);
                throw;
            }

            if (result.IsCompleted)
                throw new SshProtocolException(DisconnectReason.ConnectionLost,
                    "Connection closed during version exchange");
        }
    }

    #endregion

    #region Packet I/O

    /// <summary>
    /// Sends an SSH packet.
    /// </summary>
    /// <param name="payload">Packet payload (message type + data).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendPacketInternalAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask SendPacketInternalAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // Calculate padding using the cipher's block size
        var blockSize = _sendCipher.BlockSize;
        var paddingLength = CalculatePadding(payload.Length, blockSize, _sendCipher.IsAead);

        // Build packet: packet_length (4) || padding_length (1) || payload || padding
        var packetLength = 1 + payload.Length + paddingLength;
        var totalSize = 4 + packetLength;

        if (totalSize > MaxPacketLength)
            throw new SshProtocolException(DisconnectReason.ProtocolError, "Packet too large");

        var packet = _sendBuffer.AsSpan(0, totalSize);

        // Write packet_length
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)packetLength);

        // Write padding_length
        packet[4] = (byte)paddingLength;

        // Write payload
        payload.Span.CopyTo(packet[5..]);

        // Write random padding
        RandomNumberGenerator.Fill(packet[(5 + payload.Length)..totalSize]);

        // Encrypt
        byte[] sendData;
        if (_sendCipher == NullCipher.Instance)
        {
            sendData = packet.ToArray();
        }
        else
        {
            var encryptedSize = totalSize + _sendCipher.TagSize;
            var encrypted = new byte[encryptedSize];
            var written = _sendCipher.Encrypt(_sendSequence, packet, encrypted);
            sendData = encrypted.AsSpan(0, written).ToArray();
        }

        // Send
        await _stream.WriteAsync(sendData, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        _sendSequence++;
    }

    /// <summary>
    /// Sends an SSH message.
    /// </summary>
    public async ValueTask SendMessageAsync(ISshMessage message, CancellationToken cancellationToken = default)
    {
        // Size includes message type byte + payload
        var size = 1 + message.GetSize();
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            // Write message type byte
            buffer[0] = (byte)message.MessageType;
            // Write payload after message type
            var written = message.WriteTo(buffer.AsSpan(1));
            await SendPacketAsync(buffer.AsMemory(0, 1 + written), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Receives an SSH packet.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Packet payload (message type + data).</returns>
    public async ValueTask<byte[]> ReceivePacketAsync(CancellationToken cancellationToken = default)
    {
        if (_receiveCipher == NullCipher.Instance)
        {
            return await ReceiveUnencryptedPacketAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return await ReceiveEncryptedPacketAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<byte[]> ReceiveUnencryptedPacketAsync(CancellationToken cancellationToken)
    {
        // Read packet length (4 bytes)
        var lengthBytes = await ReadExactAsync(4, cancellationToken).ConfigureAwait(false);
        var packetLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

        if (packetLength < MinPacketLength - 4 || packetLength > MaxPacketLength - 4)
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Invalid packet length: {packetLength}");

        // Read rest of packet
        var packetData = await ReadExactAsync((int)packetLength, cancellationToken).ConfigureAwait(false);

        var paddingLength = packetData[0];
        if (paddingLength < MinPaddingLength || paddingLength >= packetLength)
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Invalid padding length: {paddingLength}");

        var payloadLength = (int)packetLength - 1 - paddingLength;
        var payload = packetData.AsSpan(1, payloadLength).ToArray();

        _receiveSequence++;
        return payload;
    }

    private async ValueTask<byte[]> ReceiveEncryptedPacketAsync(CancellationToken cancellationToken)
    {
        // For AEAD ciphers, first read encrypted length (4 bytes)
        var encryptedLength = await ReadExactAsync(4, cancellationToken).ConfigureAwait(false);
        var packetLength = _receiveCipher.DecryptLength(_receiveSequence, encryptedLength);

        if (packetLength < MinPacketLength - 4 || packetLength > MaxPacketLength - 4)
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Invalid packet length: {packetLength}");

        // Read encrypted payload + tag
        var remainingSize = (int)packetLength + _receiveCipher.TagSize;
        var encryptedData = await ReadExactAsync(remainingSize, cancellationToken).ConfigureAwait(false);

        // Combine length + payload + tag for decryption
        var fullCiphertext = new byte[4 + remainingSize];
        encryptedLength.CopyTo(fullCiphertext.AsSpan(0, 4));
        encryptedData.CopyTo(fullCiphertext.AsSpan(4));

        // Decrypt
        var decrypted = _receiveCipher.Decrypt(_receiveSequence, fullCiphertext, _receiveBuffer);
        if (decrypted < 0)
            throw new SshProtocolException(DisconnectReason.MacError, "MAC verification failed");

        var paddingLength = _receiveBuffer[4];
        if (paddingLength < MinPaddingLength || paddingLength >= packetLength)
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Invalid padding length: {paddingLength}");

        var payloadLength = (int)packetLength - 1 - paddingLength;
        var payload = _receiveBuffer.AsSpan(5, payloadLength).ToArray();

        _receiveSequence++;
        return payload;
    }

    private async ValueTask<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var readResult = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = readResult.Buffer;

            if (buffer.IsEmpty && readResult.IsCompleted)
                throw new SshProtocolException(DisconnectReason.ConnectionLost, "Connection closed");

            var toCopy = Math.Min((int)buffer.Length, count - offset);
            buffer.Slice(0, toCopy).CopyTo(result.AsSpan(offset));
            offset += toCopy;

            _reader.AdvanceTo(buffer.GetPosition(toCopy));
        }

        return result;
    }

    /// <summary>
    /// Receives and parses an SSH message.
    /// </summary>
    public async ValueTask<ISshMessage> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        var payload = await ReceivePacketAsync(cancellationToken).ConfigureAwait(false);
        return MessageParser.Parse(payload);
    }

    private static int CalculatePadding(int payloadLength, int blockSize, bool isAead)
    {
        // For AEAD ciphers (like ChaCha20-Poly1305), packet_length is encrypted separately,
        // so padding alignment only considers: padding_length (1) + payload + padding
        // For non-AEAD ciphers: packet_length (4) + padding_length (1) + payload + padding
        // Per OpenSSH PROTOCOL.chacha20poly1305: "The packet_length itself is *not*
        // included in this padding calculation, since it is encrypted separately."
        var baseSize = isAead
            ? 1 + payloadLength   // padding_length + payload
            : 5 + payloadLength;  // packet_length + padding_length + payload
        var padding = blockSize - (baseSize % blockSize);
        if (padding < MinPaddingLength)
            padding += blockSize;
        return padding;
    }

    #endregion

    #region Key Exchange Support

    /// <summary>
    /// Gets the client version string bytes (for exchange hash).
    /// </summary>
    public ReadOnlySpan<byte> ClientVersion => _clientVersion;

    /// <summary>
    /// Gets the server version string bytes (for exchange hash).
    /// </summary>
    public ReadOnlySpan<byte> ServerVersion => _serverVersion;

    /// <summary>
    /// Activates new encryption keys after key exchange.
    /// </summary>
    /// <param name="sendCipher">Cipher for outgoing packets.</param>
    /// <param name="receiveCipher">Cipher for incoming packets.</param>
    /// <param name="exchangeHash">Exchange hash H (becomes session ID on first key exchange).</param>
    public void ActivateKeys(ISshCipher sendCipher, ISshCipher receiveCipher, ReadOnlySpan<byte> exchangeHash)
    {
        _sendCipher = sendCipher ?? throw new ArgumentNullException(nameof(sendCipher));
        _receiveCipher = receiveCipher ?? throw new ArgumentNullException(nameof(receiveCipher));

        // First exchange hash becomes the session ID
        _sessionId ??= exchangeHash.ToArray();
    }

    /// <summary>
    /// Resets sequence numbers (for testing).
    /// </summary>
    internal void ResetSequenceNumbers()
    {
        _sendSequence = 0;
        _receiveSequence = 0;
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        await _reader.CompleteAsync().ConfigureAwait(false);
        await _writer.CompleteAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    #endregion
}
