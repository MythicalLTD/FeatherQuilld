namespace FeatherQuilld.Utils.Startup;

public enum BootStepStatus
{
    Success,
    Warning,
    Skipped,
    Failed,
}

public sealed class BootStepResult
{
    public BootStepStatus Status { get; set; } = BootStepStatus.Success;
    public List<string> Details { get; init; } = [];

    public static BootStepResult Merge(params BootStepResult[] results)
    {
        var merged = new BootStepResult();
        if (results.Any(r => r.Status == BootStepStatus.Failed))
            merged.Status = BootStepStatus.Failed;
        else if (results.Any(r => r.Status == BootStepStatus.Warning))
            merged.Status = BootStepStatus.Warning;
        else if (results.All(r => r.Status == BootStepStatus.Skipped))
            merged.Status = BootStepStatus.Skipped;

        return merged;
    }
}

/// <summary>Collects boot-time messages without writing to the log file/console.</summary>
public sealed class BootReporter
{
    private readonly List<string> _details = [];

    public IReadOnlyList<string> Details => _details;

    public void Detail(string message) => _details.Add(message);
}

public sealed class BootSummary
{
    public required string AppName { get; init; }
    public required string Version { get; init; }
    public required string ListenAddress { get; init; }
    public required string ConfigPath { get; init; }
    public int PluginCount { get; init; }
    public IReadOnlyList<string> Plugins { get; init; } = [];
    public bool DocsEnabled { get; init; }
}
