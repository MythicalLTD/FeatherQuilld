namespace FeatherQuilld.Plugins.Events;

public sealed class TransferOutgoingBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required string UploadUrl { get; init; }
    public required bool StartOnCompletion { get; init; }
}

public sealed class TransferOutgoingAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class TransferIncomingBeforeEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public required bool StartOnCompletion { get; init; }
}

public sealed class TransferIncomingAfterEvent
{
    public required Guid WebSpaceUuid { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
