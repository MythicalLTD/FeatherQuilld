using System.Collections.Concurrent;

namespace d0x2a.EmbeddedSsh.Connection;

/// <summary>
/// Manages SSH channels within a connection.
/// </summary>
public sealed class ChannelManager
{
    private readonly ConcurrentDictionary<uint, SshChannel> _channels = new();
    private uint _nextChannelId;
    private readonly object _channelIdLock = new();

    /// <summary>
    /// Default initial window size (2MB).
    /// </summary>
    public const uint DefaultWindowSize = 2 * 1024 * 1024;

    /// <summary>
    /// Default maximum packet size (32KB).
    /// </summary>
    public const uint DefaultMaxPacketSize = 32 * 1024;

    /// <summary>
    /// Gets the number of active channels.
    /// </summary>
    public int ActiveChannelCount => _channels.Count;

    /// <summary>
    /// Allocates a new channel ID.
    /// </summary>
    public uint AllocateChannelId()
    {
        lock (_channelIdLock)
        {
            return _nextChannelId++;
        }
    }

    /// <summary>
    /// Registers a channel.
    /// </summary>
    public void RegisterChannel(SshChannel channel)
    {
        if (!_channels.TryAdd(channel.LocalChannelId, channel))
        {
            throw new InvalidOperationException($"Channel {channel.LocalChannelId} already exists");
        }
    }

    /// <summary>
    /// Gets a channel by its local ID.
    /// </summary>
    public SshChannel? GetChannel(uint localChannelId)
    {
        _channels.TryGetValue(localChannelId, out var channel);
        return channel;
    }

    /// <summary>
    /// Removes a channel.
    /// </summary>
    public bool RemoveChannel(uint localChannelId)
    {
        return _channels.TryRemove(localChannelId, out _);
    }

    /// <summary>
    /// Gets all active channels.
    /// </summary>
    public IEnumerable<SshChannel> GetAllChannels()
    {
        return _channels.Values;
    }

    /// <summary>
    /// Closes all channels.
    /// </summary>
    public async ValueTask CloseAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels.Values)
        {
            await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        _channels.Clear();
    }
}
