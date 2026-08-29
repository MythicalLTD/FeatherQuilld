namespace FeatherQuilld.Plugins.Events;

public sealed class SftpAuthBeforeEvent
{
    public required string Username { get; init; }
    public required string AuthMethod { get; init; }
}

public sealed class SftpAuthAfterEvent
{
    public required string Username { get; init; }
    public required string AuthMethod { get; init; }
    public Guid? WebSpaceUuid { get; init; }
    public bool Authenticated { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpSessionOpenBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Username { get; init; }
}

public sealed class SftpSessionOpenAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Username { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpSessionCloseAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Username { get; init; }
}

public sealed class SftpWriteBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class SftpWriteAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpMkdirBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class SftpMkdirAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpRmdirBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class SftpRmdirAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpRemoveBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class SftpRemoveAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpRenameBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
}

public sealed class SftpRenameAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class SftpSetstatBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class SftpSetstatAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
