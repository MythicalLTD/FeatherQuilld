namespace FeatherQuilld.Plugins.Events;

public sealed class ScheduleExecuteBeforeEvent
{
    public required string WebSpaceUuid { get; init; }
    public required int ScheduleId { get; init; }
}

public sealed class ScheduleExecuteAfterEvent
{
    public required string WebSpaceUuid { get; init; }
    public required int ScheduleId { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class ScheduleTaskExecuteBeforeEvent
{
    public required string WebSpaceUuid { get; init; }
    public required int ScheduleId { get; init; }
    public required string Action { get; init; }
}

public sealed class ScheduleTaskExecuteAfterEvent
{
    public required string WebSpaceUuid { get; init; }
    public required int ScheduleId { get; init; }
    public required string Action { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
