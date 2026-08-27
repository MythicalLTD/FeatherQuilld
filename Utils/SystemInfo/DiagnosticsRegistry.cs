namespace FeatherQuilld.Utils.SystemInfo;

/// <summary>Stores boot + on-demand diagnostic check results for the diagnostics API.</summary>
public sealed class DiagnosticsRegistry
{
    private readonly object _gate = new();
    private IReadOnlyList<DiagnosticCheck> _bootChecks = Array.Empty<DiagnosticCheck>();
    private DateTimeOffset? _bootCheckedAt;
    private IReadOnlyList<DiagnosticCheck> _liveChecks = Array.Empty<DiagnosticCheck>();
    private DateTimeOffset? _liveCheckedAt;

    public void SetBootChecks(IEnumerable<DiagnosticCheck> checks)
    {
        lock (_gate)
        {
            _bootChecks = checks.ToList();
            _bootCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    public void SetLiveChecks(IEnumerable<DiagnosticCheck> checks)
    {
        lock (_gate)
        {
            _liveChecks = checks.ToList();
            _liveCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    public DiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DiagnosticsSnapshot(
                BootCheckedAt: _bootCheckedAt,
                BootChecks: _bootChecks,
                LiveCheckedAt: _liveCheckedAt,
                LiveChecks: _liveChecks.Count > 0 ? _liveChecks : _bootChecks);
        }
    }
}

public sealed record DiagnosticCheck(
    string Id,
    string Status,
    string Message,
    string? Detail = null);

public sealed record DiagnosticsSnapshot(
    DateTimeOffset? BootCheckedAt,
    IReadOnlyList<DiagnosticCheck> BootChecks,
    DateTimeOffset? LiveCheckedAt,
    IReadOnlyList<DiagnosticCheck> LiveChecks);
