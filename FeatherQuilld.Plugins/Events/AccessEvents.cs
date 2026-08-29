namespace FeatherQuilld.Plugins.Events;

public sealed class AccessDeauthorizeBeforeEvent
{
    public required Guid UserUuid { get; init; }
    public required IReadOnlyList<Guid> WebSpaceUuids { get; init; }
}

public sealed class AccessDeauthorizeAfterEvent
{
    public required Guid UserUuid { get; init; }
    public required IReadOnlyList<Guid> WebSpaceUuids { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class AccessSetPermissionsBeforeEvent
{
    public required Guid UserUuid { get; init; }
    public required Guid WebSpaceUuid { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
}

public sealed class AccessSetPermissionsAfterEvent
{
    public required Guid UserUuid { get; init; }
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
