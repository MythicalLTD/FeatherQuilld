namespace FeatherQuilld.Plugins.Events;

/// <summary>Event bus that never invokes handlers — safe default when plugins are disabled.</summary>
public sealed class NoOpEventBus : IEventBus
{
    public static NoOpEventBus Instance { get; } = new();

    private NoOpEventBus()
    {
    }

    public IDisposable On<TEvent>(Func<TEvent, HookResult> handler, int priority = 0)
        where TEvent : class =>
        EmptySubscription.Instance;

    public IDisposable On<TEvent>(Func<TEvent, CancellationToken, Task<HookResult>> handler, int priority = 0)
        where TEvent : class =>
        EmptySubscription.Instance;

    public HookResult Emit<TEvent>(TEvent evt) where TEvent : class => HookResult.Continue();

    public Task<HookResult> EmitAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : class =>
        Task.FromResult(HookResult.Continue());

    private sealed class EmptySubscription : IDisposable
    {
        public static readonly EmptySubscription Instance = new();
        public void Dispose()
        {
        }
    }
}
