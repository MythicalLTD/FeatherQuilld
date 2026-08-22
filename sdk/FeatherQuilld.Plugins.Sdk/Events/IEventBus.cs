namespace FeatherQuilld.Plugins.Sdk.Events;

/// <summary>Pub/sub event bus with priority-ordered hooks.</summary>
public interface IEventBus
{
    IDisposable On<TEvent>(Func<TEvent, HookResult> handler, int priority = 0)
        where TEvent : class;

    IDisposable On<TEvent>(Func<TEvent, CancellationToken, Task<HookResult>> handler, int priority = 0)
        where TEvent : class;

    HookResult Emit<TEvent>(TEvent evt) where TEvent : class;

    Task<HookResult> EmitAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : class;
}
