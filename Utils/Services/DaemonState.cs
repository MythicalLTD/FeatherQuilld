namespace FeatherQuilld.Utils.Services;

/// <summary>
/// Tracks daemon runtime state for health probes and maintenance mode.
/// </summary>
public sealed class DaemonState
{
    private volatile bool _maintenanceMode;
    private volatile bool _panelReachable = true;
    private volatile string? _lastPanelError;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public bool MaintenanceMode
    {
        get => _maintenanceMode;
        set => _maintenanceMode = value;
    }

    public bool PanelReachable
    {
        get => _panelReachable;
        set => _panelReachable = value;
    }

    public string? LastPanelError
    {
        get => _lastPanelError;
        set => _lastPanelError = value;
    }

    public long UptimeSeconds =>
        (long)Math.Max(0, (DateTimeOffset.UtcNow - StartedAt).TotalSeconds);

    public bool IsHealthy => !_maintenanceMode;

    public string HealthStatus => IsHealthy ? "healthy" : "unhealthy";
}
