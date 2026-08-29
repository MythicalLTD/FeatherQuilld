namespace FeatherQuilld.Plugins.Events;

public sealed class ProxyRebuildBeforeEvent
{
    public required int WebSpaceCount { get; init; }
    public required string Provider { get; init; }
}

public sealed class ProxyRebuildAfterEvent
{
    public required int WebSpaceCount { get; init; }
    public required string Provider { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class AcmeEnsureCertsBeforeEvent
{
    public required IReadOnlyList<string> Domains { get; init; }
}

public sealed class AcmeEnsureCertsAfterEvent
{
    public required IReadOnlyList<string> Domains { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class AcmeIssueBeforeEvent
{
    public required string Domain { get; init; }
}

public sealed class AcmeIssueAfterEvent
{
    public required string Domain { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class StaticFileSyncBeforeEvent
{
    public required int WebSpaceCount { get; init; }
}

public sealed class StaticFileSyncAfterEvent
{
    public required int WebSpaceCount { get; init; }
    public Exception? Error { get; init; }
    public bool Success => Error is null;
}
