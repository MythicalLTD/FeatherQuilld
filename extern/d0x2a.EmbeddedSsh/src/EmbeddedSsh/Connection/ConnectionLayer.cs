using System.Threading.Channels;
using d0x2a.EmbeddedSsh.Protocol;
using d0x2a.EmbeddedSsh.Protocol.Messages;
using d0x2a.EmbeddedSsh.Transport;

namespace d0x2a.EmbeddedSsh.Connection;

/// <summary>
/// SSH connection layer (RFC 4254).
/// Manages channels and handles connection-layer messages.
/// </summary>
public sealed class ConnectionLayer
{
    private readonly TransportLayer _transport;
    private readonly ChannelManager _channelManager;
    private readonly Channel<SshChannel> _newChannelChannel;
    private readonly Func<SshChannel, ChannelRequestMessage, CancellationToken, ValueTask<bool>>? _channelRequestHandler;

    /// <summary>
    /// Event raised when a new channel is opened.
    /// </summary>
    public event Func<SshChannel, ValueTask>? ChannelOpened;

    /// <summary>
    /// Event raised when a channel request is received.
    /// </summary>
    public event Func<SshChannel, ChannelRequestMessage, CancellationToken, ValueTask<bool>>? ChannelRequestReceived;

    public ConnectionLayer(
        TransportLayer transport,
        ChannelManager? channelManager = null,
        Func<SshChannel, ChannelRequestMessage, CancellationToken, ValueTask<bool>>? channelRequestHandler = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _channelManager = channelManager ?? new ChannelManager();
        _channelRequestHandler = channelRequestHandler;

        _newChannelChannel = Channel.CreateUnbounded<SshChannel>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
    }

    /// <summary>
    /// Gets the channel manager.
    /// </summary>
    public ChannelManager Channels => _channelManager;

    /// <summary>
    /// Waits for a new channel to be opened.
    /// </summary>
    public async ValueTask<SshChannel> AcceptChannelAsync(CancellationToken cancellationToken = default)
    {
        return await _newChannelChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes incoming connection-layer messages.
    /// Call this in a loop to handle channel operations.
    /// </summary>
    public async ValueTask ProcessMessageAsync(ISshMessage message, CancellationToken cancellationToken = default)
    {
        switch (message)
        {
            case ChannelOpenMessage open:
                await HandleChannelOpenAsync(open, cancellationToken).ConfigureAwait(false);
                break;

            case ChannelDataMessage data:
                await HandleChannelDataAsync(data).ConfigureAwait(false);
                break;

            case ChannelExtendedDataMessage extData:
                await HandleChannelExtendedDataAsync(extData).ConfigureAwait(false);
                break;

            case ChannelWindowAdjustMessage adjust:
                HandleWindowAdjust(adjust);
                break;

            case ChannelEofMessage eof:
                HandleEof(eof);
                break;

            case ChannelCloseMessage close:
                await HandleCloseAsync(close, cancellationToken).ConfigureAwait(false);
                break;

            case ChannelRequestMessage request:
                await HandleChannelRequestAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            case GlobalRequestMessage global:
                await HandleGlobalRequestAsync(global, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new SshProtocolException(DisconnectReason.ProtocolError,
                    $"Unexpected message in connection layer: {message.MessageType}");
        }
    }

    private async ValueTask HandleChannelOpenAsync(ChannelOpenMessage open, CancellationToken cancellationToken)
    {
        // Only support session channels for now
        if (open.ChannelType != "session")
        {
            await SendChannelOpenFailureAsync(
                open.SenderChannel,
                ChannelOpenFailureReason.UnknownChannelType,
                $"Channel type '{open.ChannelType}' not supported",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Allocate channel
        var localChannelId = _channelManager.AllocateChannelId();
        var channel = new SshChannel(
            _transport,
            _channelManager,
            localChannelId,
            open.ChannelType,
            ChannelManager.DefaultWindowSize);

        channel.ConfirmOpen(open.SenderChannel, open.InitialWindowSize, open.MaximumPacketSize);
        _channelManager.RegisterChannel(channel);

        // Send confirmation
        var confirmation = new ChannelOpenConfirmationMessage
        {
            RecipientChannel = open.SenderChannel,
            SenderChannel = localChannelId,
            InitialWindowSize = ChannelManager.DefaultWindowSize,
            MaximumPacketSize = ChannelManager.DefaultMaxPacketSize
        };

        await _transport.SendMessageAsync(confirmation, cancellationToken).ConfigureAwait(false);

        // Notify about new channel
        if (ChannelOpened != null)
        {
            await ChannelOpened(channel).ConfigureAwait(false);
        }

        await _newChannelChannel.Writer.WriteAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleChannelDataAsync(ChannelDataMessage data)
    {
        var channel = _channelManager.GetChannel(data.RecipientChannel);
        if (channel != null)
        {
            await channel.OnDataReceivedAsync(data.Data).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleChannelExtendedDataAsync(ChannelExtendedDataMessage extData)
    {
        var channel = _channelManager.GetChannel(extData.RecipientChannel);
        if (channel != null)
        {
            await channel.OnExtendedDataReceivedAsync((uint)extData.DataTypeCode, extData.Data).ConfigureAwait(false);
        }
    }

    private void HandleWindowAdjust(ChannelWindowAdjustMessage adjust)
    {
        var channel = _channelManager.GetChannel(adjust.RecipientChannel);
        channel?.OnWindowAdjust(adjust.BytesToAdd);
    }

    private void HandleEof(ChannelEofMessage eof)
    {
        var channel = _channelManager.GetChannel(eof.RecipientChannel);
        channel?.OnEofReceived();
    }

    private async ValueTask HandleCloseAsync(ChannelCloseMessage close, CancellationToken cancellationToken)
    {
        var channel = _channelManager.GetChannel(close.RecipientChannel);
        if (channel != null)
        {
            await channel.OnCloseReceivedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleChannelRequestAsync(ChannelRequestMessage request, CancellationToken cancellationToken)
    {
        var channel = _channelManager.GetChannel(request.RecipientChannel);
        if (channel == null)
        {
            if (request.WantReply)
            {
                await SendChannelFailureAsync(request.RecipientChannel, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var success = false;

        // Handle known request types
        switch (request.RequestType)
        {
            case "pty-req":
                if (request.Term != null)
                {
                    channel.Pty = new PtySettings(
                        request.Term,
                        request.TerminalWidthChars,
                        request.TerminalHeightRows,
                        request.TerminalWidthPixels,
                        request.TerminalHeightPixels,
                        request.TerminalModes);
                    success = true;
                }
                break;

            case "env":
                if (request.EnvName != null && request.EnvValue != null)
                {
                    channel.Environment[request.EnvName] = request.EnvValue;
                    success = true;
                }
                break;

            case "shell":
                channel.ShellRequested = true;
                success = true;
                break;

            case "exec":
                if (request.Command != null)
                {
                    channel.Command = request.Command;
                    success = true;
                }
                break;

            case "window-change":
                if (channel.Pty != null)
                {
                    channel.Pty = channel.Pty with
                    {
                        WidthChars = request.TerminalWidthChars,
                        HeightRows = request.TerminalHeightRows,
                        WidthPixels = request.TerminalWidthPixels,
                        HeightPixels = request.TerminalHeightPixels
                    };
                    success = true;
                }
                break;

            case "signal":
                // Signal handling - application specific
                success = true;
                break;

            default:
                // Delegate to handler
                if (_channelRequestHandler != null)
                {
                    success = await _channelRequestHandler(channel, request, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (ChannelRequestReceived != null)
                {
                    success = await ChannelRequestReceived(channel, request, cancellationToken)
                        .ConfigureAwait(false);
                }
                break;
        }

        if (request.WantReply)
        {
            if (success)
            {
                await SendChannelSuccessAsync(request.RecipientChannel, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendChannelFailureAsync(request.RecipientChannel, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask HandleGlobalRequestAsync(GlobalRequestMessage global, CancellationToken cancellationToken)
    {
        // We don't support any global requests currently
        if (global.WantReply)
        {
            await _transport.SendMessageAsync(RequestFailureMessage.Instance, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask SendChannelOpenFailureAsync(
        uint recipientChannel,
        ChannelOpenFailureReason reason,
        string description,
        CancellationToken cancellationToken)
    {
        var message = new ChannelOpenFailureMessage
        {
            RecipientChannel = recipientChannel,
            ReasonCode = reason,
            Description = description,
            LanguageTag = ""
        };

        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendChannelSuccessAsync(uint recipientChannel, CancellationToken cancellationToken)
    {
        var message = new ChannelSuccessMessage { RecipientChannel = recipientChannel };
        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendChannelFailureAsync(uint recipientChannel, CancellationToken cancellationToken)
    {
        var message = new ChannelFailureMessage { RecipientChannel = recipientChannel };
        await _transport.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
