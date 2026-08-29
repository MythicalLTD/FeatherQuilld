namespace FeatherQuilld.Plugins.Events;

public sealed class BackupCreateBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required bool StopDuringBackup { get; init; }
}

public sealed class BackupCreateAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public object? Result { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class BackupRestoreBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required Guid BackupUuid { get; init; }
}

public sealed class BackupRestoreAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required Guid BackupUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class BackupDeleteBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required Guid BackupUuid { get; init; }
}

public sealed class BackupDeleteAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required Guid BackupUuid { get; init; }
    public bool Deleted { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class BackupImportBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
}

public sealed class BackupImportAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public object? Result { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class BackupReconcileBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
}

public sealed class BackupReconcileAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public int? Count { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
