namespace FeatherQuilld.Plugins.Events;

/// <summary>Thrown when a Before-hook returns <see cref="HookAction.Cancel"/>.</summary>
public sealed class PluginHookCancelledException : Exception
{
    public string EventName { get; }

    public PluginHookCancelledException(string eventName)
        : base($"Plugin hook cancelled: {eventName}")
    {
        EventName = eventName;
    }

    public PluginHookCancelledException(string eventName, string? message)
        : base(message ?? $"Plugin hook cancelled: {eventName}")
    {
        EventName = eventName;
    }
}
