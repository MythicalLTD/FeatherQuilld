using FeatherQuilld.Plugins.Sdk.Events;

namespace FeatherQuilld.Utils.Plugins.Events;

/// <summary>Priority-ordered event bus with cancel/replace semantics.</summary>
public sealed class EventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<HandlerEntry>> _handlers = [];

    public IDisposable On<TEvent>(Func<TEvent, HookResult> handler, int priority = 0)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(typeof(TEvent), priority, (evt, _) => Task.FromResult(handler((TEvent)evt)));
    }

    public IDisposable On<TEvent>(Func<TEvent, CancellationToken, Task<HookResult>> handler, int priority = 0)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(typeof(TEvent), priority, (evt, ct) => handler((TEvent)evt, ct));
    }

    public HookResult Emit<TEvent>(TEvent evt) where TEvent : class =>
        EmitAsync(evt).GetAwaiter().GetResult();

    public async Task<HookResult> EmitAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(evt);

        HandlerEntry[] snapshot;
        lock (_gate)
            snapshot = _handlers.GetValueOrDefault(typeof(TEvent))?.ToArray() ?? [];

        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await entry.Handler(evt, cancellationToken).ConfigureAwait(false);
            if (result.Action != HookAction.Continue)
                return result;
        }

        return HookResult.Continue();
    }

    private IDisposable Register(Type eventType, int priority, Func<object, CancellationToken, Task<HookResult>> handler)
    {
        var entry = new HandlerEntry(priority, handler);

        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
            {
                list = [];
                _handlers[eventType] = list;
            }

            list.Add(entry);
            list.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(eventType, out var list))
                    list.Remove(entry);
            }
        });
    }

    private sealed record HandlerEntry(int Priority, Func<object, CancellationToken, Task<HookResult>> Handler);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
