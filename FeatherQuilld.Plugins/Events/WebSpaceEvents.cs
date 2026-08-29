namespace FeatherQuilld.Plugins.Events;

public sealed class WebSpaceCreateBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
}

public sealed class WebSpaceCreateAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpaceSyncBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
}

public sealed class WebSpaceSyncAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpaceDeleteBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
}

public sealed class WebSpaceDeleteAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public bool Deleted { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpaceReinstallBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required bool WipeFiles { get; init; }
    public required bool StartOnCompletion { get; init; }
}

public sealed class WebSpaceReinstallAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpaceSslRenewBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
}

public sealed class WebSpaceSslRenewAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public object? Result { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpacePowerBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Action { get; init; }
}

public sealed class WebSpacePowerAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Action { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpaceConsoleCommandBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Command { get; init; }
}

public sealed class WebSpaceConsoleCommandAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Command { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class WebSpaceExecBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Command { get; init; }
}

public sealed class WebSpaceExecAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Command { get; init; }
    public long? ExitCode { get; init; }
    public string? Output { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
