namespace FeatherQuilld.Plugins.Events;

public sealed class FileListBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public string? Directory { get; init; }
}

public sealed class FileListAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public string? Directory { get; init; }
    public IReadOnlyList<object>? Entries { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileReadBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class FileReadAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public string? Contents { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileWriteBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class FileWriteAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileCreateDirectoryBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class FileCreateDirectoryAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileRenameBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
}

public sealed class FileRenameAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileCopyBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string From { get; init; }
    public string? To { get; init; }
}

public sealed class FileCopyAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string From { get; init; }
    public string? To { get; init; }
    public string? ResultPath { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileDeleteBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
}

public sealed class FileDeleteAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileUploadBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Directory { get; init; }
    public required string FileName { get; init; }
}

public sealed class FileUploadAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Directory { get; init; }
    public required string FileName { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileCompressBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
}

public sealed class FileCompressAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public string? ArchivePath { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileDecompressBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
}

public sealed class FileDecompressAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Path { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FileChmodBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required IReadOnlyList<(string File, string Mode)> Entries { get; init; }
}

public sealed class FileChmodAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class FilePullBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Url { get; init; }
    public string? Directory { get; init; }
}

public sealed class FilePullAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string Url { get; init; }
    public string? ResultPath { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
