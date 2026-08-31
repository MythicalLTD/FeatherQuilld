namespace FeatherQuilld.Utils.Startup;

public enum ConfigureStepStatus
{
    Success,
    Warning,
    Failed,
    Skipped,
}

public sealed class ConfigureStepResult
{
    public ConfigureStepStatus Status { get; set; } = ConfigureStepStatus.Success;
    public List<string> Details { get; init; } = [];
}

public sealed class ConfigureReporter
{
    private readonly List<string> _details = [];
    private volatile string? _status;

    public IReadOnlyList<string> Details => _details;

    public string? Status => _status;

    public void Detail(string message) => _details.Add(message);

    public void Progress(string message) => _status = message;
}

public sealed class ConfigureSummary
{
    public required Guid NodeUuid { get; init; }
    public required string PanelUrl { get; init; }
    public required string ConfigPath { get; init; }
    public required int ApiPort { get; init; }
    public required string Version { get; init; }
    public bool SftpEnabled { get; init; }
    public int SftpPort { get; init; }
    public bool FtpEnabled { get; init; }
    public int FtpPort { get; init; }
    public bool ServiceInstalled { get; init; }
    public bool ServiceStarted { get; init; }
    public bool ServiceSkipped { get; init; }
}
