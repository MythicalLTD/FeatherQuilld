using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FeatherQuilld.Utils.SystemInfo;

/// <summary>WebSocket fan-out for host package install/remove output.</summary>
public sealed class SystemPackageWsHub
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ConcurrentDictionary<int, WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, PackageOperationState> _operations = new();
    private readonly ConcurrentDictionary<int, int> _socketReplayPosition = new();
    private int _nextSocketId;

    public int Register(WebSocket socket)
    {
        var id = Interlocked.Increment(ref _nextSocketId);
        _sockets[id] = socket;
        return id;
    }

    public void Unregister(int socketId)
    {
        _sockets.TryRemove(socketId, out _);
        _socketReplayPosition.TryRemove(socketId, out _);
    }

    public async Task CloseAllAsync(string reason = "shutting down")
    {
        foreach (var (socketId, socket) in _sockets.ToArray())
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        reason,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // ignore close races during shutdown
                }
            }

            Unregister(socketId);
        }
    }

    public bool IsOperationActive(string packageId) =>
        _operations.TryGetValue(NormalizeId(packageId), out var state) && state.Active;

    public string? GetBufferedOutput(string packageId)
    {
        return _operations.TryGetValue(NormalizeId(packageId), out var state)
            ? state.Buffer.ToString()
            : null;
    }

    public void BeginOperation(string packageId, string action)
    {
        var id = NormalizeId(packageId);
        _operations[id] = new PackageOperationState(action, active: true);
    }

    public Task SendStartedAsync(string packageId, CancellationToken ct = default) =>
        BroadcastAsync("package started", [NormalizeId(packageId)], ct);

    public async Task SendOutputAsync(string packageId, string chunk, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        var id = NormalizeId(packageId);
        var state = _operations.GetOrAdd(id, _ => new PackageOperationState("install", active: true));
        state.Append(chunk);

        await BroadcastAsync("package output", [id, chunk], ct).ConfigureAwait(false);
    }

    public async Task SendCompletedAsync(string packageId, CancellationToken ct = default)
    {
        var id = NormalizeId(packageId);
        if (_operations.TryGetValue(id, out var state))
            state.Active = false;

        await BroadcastAsync("package completed", [id], ct).ConfigureAwait(false);
    }

    public async Task SendFailedAsync(string packageId, string message, CancellationToken ct = default)
    {
        var id = NormalizeId(packageId);
        if (_operations.TryGetValue(id, out var state))
            state.Active = false;

        await BroadcastAsync("package failed", [id, message], ct);
    }

    public async Task ReplayActiveOperationsAsync(int socketId, WebSocket socket, CancellationToken ct = default)
    {
        foreach (var (packageId, state) in _operations.ToArray())
        {
            if (!state.Active)
                continue;

            await SendToSocketAsync(socket, "package started", [packageId], ct).ConfigureAwait(false);

            var replayFrom = _socketReplayPosition.GetOrAdd(socketId, 0);
            if (state.Buffer.Length <= replayFrom)
                continue;

            var buffered = state.Buffer.ToString(replayFrom, state.Buffer.Length - replayFrom);
            _socketReplayPosition[socketId] = state.Buffer.Length;
            if (!string.IsNullOrWhiteSpace(buffered))
                await SendToSocketAsync(socket, "package output", [packageId, buffered], ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastAsync(string eventName, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (_sockets.IsEmpty)
            return;

        foreach (var (socketId, socket) in _sockets.ToArray())
        {
            if (socket.State != WebSocketState.Open)
            {
                Unregister(socketId);
                continue;
            }

            try
            {
                await SendToSocketAsync(socket, eventName, args, ct).ConfigureAwait(false);
            }
            catch
            {
                Unregister(socketId);
            }
        }
    }

    private static async Task SendToSocketAsync(
        WebSocket socket,
        string eventName,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { Event = eventName, Args = args }, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static string NormalizeId(string packageId) =>
        (packageId ?? "").Trim().ToLowerInvariant();

    private sealed class PackageOperationState
    {
        public PackageOperationState(string action, bool active)
        {
            Action = action;
            Active = active;
        }

        public string Action { get; }
        public bool Active { get; set; }
        public StringBuilder Buffer { get; } = new();

        public void Append(string chunk) => Buffer.Append(chunk);
    }
}
