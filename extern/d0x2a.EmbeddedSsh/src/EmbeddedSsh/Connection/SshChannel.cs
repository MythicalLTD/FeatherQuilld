using System.Buffers;
using System.Threading.Channels;
using d0x2a.EmbeddedSsh.Protocol;
using d0x2a.EmbeddedSsh.Protocol.Messages;
using d0x2a.EmbeddedSsh.Transport;

namespace d0x2a.EmbeddedSsh.Connection;

/// <summary>
/// Represents an SSH channel.
/// </summary>
public sealed class SshChannel : IAsyncDisposable
{
    private readonly TransportLayer _transport;
    private readonly ChannelManager _manager;
    private readonly Channel<ReadOnlyMemory<byte>> _dataChannel;
    private readonly Channel<(uint, ReadOnlyMemory<byte>)> _extendedDataChannel;

    private long _localWindow;
    private long _remoteWindow;
    private bool _eofReceived;
    private bool _eofSent;
    private bool _closeReceived;
    private bool _closeSent;
    private bool _isDisposed;

    /// <summary>
    /// Gets the local channel ID.
    /// </summary>
    public uint LocalChannelId { get; }

    /// <summary>
    /// Gets the remote channel ID.
    /// </summary>
    public uint RemoteChannelId { get; private set; }

    /// <summary>
    /// Gets the channel type (e.g., "session").
    /// </summary>
    public string ChannelType { get; }

    /// <summary>
    /// Gets the maximum packet size for this channel.
    /// </summary>
    public uint MaxPacketSize { get; private set; }

    /// <summary>
    /// Gets whether EOF has been received.
    /// </summary>
    public bool EofReceived => _eofReceived;

    /// <summary>
    /// Gets whether the channel is closed.
    /// </summary>
    public bool IsClosed => _closeReceived || _closeSent;

    /// <summary>
    /// Optional PTY settings for session channels.
    /// </summary>
    public PtySettings? Pty { get; internal set; }

    /// <summary>
    /// Environment variables set for this channel.
    /// </summary>
    public Dictionary<string, string> Environment { get; } = new();

    /// <summary>
    /// The command to execute (for exec requests).
    /// </summary>
    public string? Command { get; internal set; }

    /// <summary>
    /// Whether a shell was requested.
    /// </summary>
    public bool ShellRequested { get; internal set; }

    internal SshChannel(
        TransportLayer transport,
        ChannelManager manager,
        uint localChannelId,
        string channelType,
        uint initialLocalWindow)
    {
        _transport = transport;
        _manager = manager;
        LocalChannelId = localChannelId;
        ChannelType = channelType;
        _localWindow = initialLocalWindow;

        _dataChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        _extendedDataChannel = Channel.CreateBounded<(uint, ReadOnlyMemory<byte>)>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
    }

    /// <summary>
    /// Completes channel initialization after receiving confirmation.
    /// </summary>
    internal void ConfirmOpen(uint remoteChannelId, uint remoteWindow, uint maxPacketSize)
    {
        RemoteChannelId = remoteChannelId;
        _remoteWindow = remoteWindow;
        MaxPacketSize = maxPacketSize;
    }

    /// <summary>
    /// Reads data from the channel.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _dataChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            await AdjustLocalWindowAsync((uint)data.Length, cancellationToken).ConfigureAwait(false);
            return data;
        }
        catch (ChannelClosedException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>
    /// Writes data to the channel.
    /// </summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_eofSent || _closeSent)
            throw new InvalidOperationException("Cannot write to channel after EOF or close");

        var offset = 0;
        while (offset < data.Length)
        {
            // Wait for window space
            while (Interlocked.Read(ref _remoteWindow) <= 0)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            // Calculate chunk size
            var available = Math.Min(
                (int)Interlocked.Read(ref _remoteWindow),
                (int)MaxPacketSize);
            var chunkSize = Math.Min(available, data.Length - offset);

            // Send data
            var chunk = data.Slice(offset, chunkSize);
            var message = new ChannelDataMessage
            {
                RecipientChannel = RemoteChannelId,
                Data = chunk
            };

            await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

            Interlocked.Add(ref _remoteWindow, -chunkSize);
            offset += chunkSize;
        }
    }

    /// <summary>
    /// Writes extended data (e.g., stderr) to the channel.
    /// </summary>
    public async ValueTask WriteExtendedAsync(
        uint dataType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (_eofSent || _closeSent)
            throw new InvalidOperationException("Cannot write to channel after EOF or close");

        var offset = 0;
        while (offset < data.Length)
        {
            while (Interlocked.Read(ref _remoteWindow) <= 0)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            var available = Math.Min(
                (int)Interlocked.Read(ref _remoteWindow),
                (int)MaxPacketSize);
            var chunkSize = Math.Min(available, data.Length - offset);

            var chunk = data.Slice(offset, chunkSize);
            var message = new ChannelExtendedDataMessage
            {
                RecipientChannel = RemoteChannelId,
                DataTypeCode = (ExtendedDataType)dataType,
                Data = chunk
            };

            await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

            Interlocked.Add(ref _remoteWindow, -chunkSize);
            offset += chunkSize;
        }
    }

    /// <summary>
    /// Sends EOF to the remote side.
    /// </summary>
    public async ValueTask SendEofAsync(CancellationToken cancellationToken = default)
    {
        if (_eofSent)
            return;

        _eofSent = true;
        var message = new ChannelEofMessage { RecipientChannel = RemoteChannelId };
        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends exit status and closes the channel.
    /// </summary>
    public async ValueTask SendExitStatusAsync(uint exitStatus, CancellationToken cancellationToken = default)
    {
        var request = new ChannelRequestMessage
        {
            RecipientChannel = RemoteChannelId,
            RequestType = "exit-status",
            WantReply = false,
            ExitStatus = exitStatus
        };

        await _transport.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the channel.
    /// </summary>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closeSent)
            return;

        // Send EOF first if not already sent
        if (!_eofSent)
        {
            await SendEofAsync(cancellationToken).ConfigureAwait(false);
        }

        _closeSent = true;
        var message = new ChannelCloseMessage { RecipientChannel = RemoteChannelId };
        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

        _manager.RemoveChannel(LocalChannelId);
    }

    /// <summary>
    /// Called when data is received.
    /// </summary>
    internal async ValueTask OnDataReceivedAsync(ReadOnlyMemory<byte> data)
    {
        await _dataChannel.Writer.WriteAsync(data).ConfigureAwait(false);
    }

    /// <summary>
    /// Called when extended data is received.
    /// </summary>
    internal async ValueTask OnExtendedDataReceivedAsync(uint dataType, ReadOnlyMemory<byte> data)
    {
        await _extendedDataChannel.Writer.WriteAsync((dataType, data)).ConfigureAwait(false);
    }

    /// <summary>
    /// Called when window adjustment is received.
    /// </summary>
    internal void OnWindowAdjust(uint bytesToAdd)
    {
        Interlocked.Add(ref _remoteWindow, bytesToAdd);
    }

    /// <summary>
    /// Called when EOF is received.
    /// </summary>
    internal void OnEofReceived()
    {
        _eofReceived = true;
        _dataChannel.Writer.Complete();
        _extendedDataChannel.Writer.Complete();
    }

    /// <summary>
    /// Called when close is received.
    /// </summary>
    internal async ValueTask OnCloseReceivedAsync(CancellationToken cancellationToken = default)
    {
        _closeReceived = true;
        _dataChannel.Writer.Complete();
        _extendedDataChannel.Writer.Complete();

        // Send close back if we haven't already
        if (!_closeSent)
        {
            await CloseAsync(cancellationToken).ConfigureAwait(false);
        }

        _manager.RemoveChannel(LocalChannelId);
    }

    /// <summary>
    /// Adjusts the local window and sends window adjust if needed.
    /// </summary>
    public async ValueTask AdjustLocalWindowAsync(uint bytesConsumed, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _localWindow, -bytesConsumed);

        // Send window adjust when window gets low
        if (Interlocked.Read(ref _localWindow) < ChannelManager.DefaultWindowSize / 2)
        {
            var adjust = ChannelManager.DefaultWindowSize;
            Interlocked.Add(ref _localWindow, adjust);

            var message = new ChannelWindowAdjustMessage
            {
                RecipientChannel = RemoteChannelId,
                BytesToAdd = adjust
            };
            await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (!_closeSent)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
    }
}

/// <summary>
/// PTY settings for a session channel.
/// </summary>
public sealed record PtySettings(
    string TerminalType,
    uint WidthChars,
    uint HeightRows,
    uint WidthPixels,
    uint HeightPixels,
    ReadOnlyMemory<byte> TerminalModes);
