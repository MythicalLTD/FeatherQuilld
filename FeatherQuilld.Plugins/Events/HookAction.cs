namespace FeatherQuilld.Plugins.Events;

/// <summary>What a hook handler wants the host to do next.</summary>
public enum HookAction
{
    Continue,
    Cancel,
    Replace,
}
