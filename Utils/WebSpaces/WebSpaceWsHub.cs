using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// Wings-style WebSocket fan-out for a WebSpace (install output, status, console).
/// </summary>
public sealed class WebSpaceWsHub
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<int, WebSocket>> _sockets = new();
    private int _nextSocketId;

    public int Register(Guid uuid, WebSocket socket)
    {
        var id = Interlocked.Increment(ref _nextSocketId);
        _sockets.GetOrAdd(uuid, _ => new ConcurrentDictionary<int, WebSocket>())[id] = socket;
        return id;
    }

    public void Unregister(Guid uuid, int socketId)
    {
        if (!_sockets.TryGetValue(uuid, out var map))
            return;

        map.TryRemove(socketId, out _);
        if (map.IsEmpty)
            _spacesTryRemove(uuid);
    }

    private void _spacesTryRemove(Guid uuid) => _sockets.TryRemove(uuid, out _);

    public async Task BroadcastAsync(
        Guid uuid,
        string eventName,
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        if (!_sockets.TryGetValue(uuid, out var map) || map.IsEmpty)
            return;

        var payload = JsonSerializer.Serialize(new { Event = eventName, Args = args }, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);

        foreach (var (socketId, socket) in map.ToArray())
        {
            if (socket.State != WebSocketState.Open)
            {
                Unregister(uuid, socketId);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch
            {
                Unregister(uuid, socketId);
            }
        }
    }

    public Task SendInstallOutputAsync(Guid uuid, string chunk, CancellationToken ct = default) =>
        BroadcastAsync(uuid, "install output", [chunk], ct);

    public Task SendInstallStartedAsync(Guid uuid, CancellationToken ct = default) =>
        BroadcastAsync(uuid, "install started", [], ct);

    public Task SendInstallCompletedAsync(Guid uuid, CancellationToken ct = default) =>
        BroadcastAsync(uuid, "install completed", [], ct);

    public Task SendInstallFailedAsync(Guid uuid, string message, CancellationToken ct = default) =>
        BroadcastAsync(uuid, "install failed", [message], ct);

    public Task SendStatusAsync(Guid uuid, string status, CancellationToken ct = default) =>
        BroadcastAsync(uuid, "status", [status], ct);
}
