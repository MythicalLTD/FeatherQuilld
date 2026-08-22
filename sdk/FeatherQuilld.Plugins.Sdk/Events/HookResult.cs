namespace FeatherQuilld.Plugins.Sdk.Events;

/// <summary>Outcome of an event hook.</summary>
public sealed class HookResult
{
    public HookAction Action { get; init; } = HookAction.Continue;
    public object? Replacement { get; init; }

    public static HookResult Continue() => new();
    public static HookResult Cancel() => new() { Action = HookAction.Cancel };
    public static HookResult Replace(object value) =>
        new() { Action = HookAction.Replace, Replacement = value };

    public bool IsCancelled => Action == HookAction.Cancel;
    public bool IsReplaced => Action == HookAction.Replace;
}
