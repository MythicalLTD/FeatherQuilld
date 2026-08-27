using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.SystemInfo;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Disk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Controllers;

/// <summary>Wings-style system / health / stats / diagnostics endpoints.</summary>
[Tags("System")]
public sealed class SystemController : ApiControllerBase
{
    private readonly Config _config;
    private readonly DaemonState _state;
    private readonly IEventBus _events;
    private readonly HostMetricsSampler _metrics;
    private readonly DiagnosticsRegistry _diagnostics;
    private readonly WebSpaceStore _spaces;

    public SystemController(
        Config config,
        DaemonState state,
        IEventBus events,
        HostMetricsSampler metrics,
        DiagnosticsRegistry diagnostics,
        WebSpaceStore spaces)
    {
        _config = config;
        _state = state;
        _events = events;
        _metrics = metrics;
        _diagnostics = diagnostics;
        _spaces = spaces;
    }

    /// <summary>Daemon identity (Calagopus Wings–compatible shape).</summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(SystemIdentityResponse), StatusCodes.Status200OK)]
    public ActionResult<SystemIdentityResponse> System()
    {
        var snap = _metrics.Capture(_config.System.Data);
        return Ok(new SystemIdentityResponse(
            Architecture: snap.Architecture,
            CpuCount: snap.CpuCount,
            KernelVersion: snap.KernelVersion,
            Os: snap.Os,
            Version: StartupBanner.Version,
            System: new SystemIdentityNested(
                Architecture: snap.Architecture,
                CpuThreads: snap.CpuCount,
                MemoryBytes: snap.MemoryTotalBytes,
                KernelVersion: snap.KernelVersion,
                Os: snap.Os,
                OsType: snap.OsType)));
    }

    /// <summary>Health probe used by FeatherPanel admin.</summary>
    [HttpGet("health")]
    [Authorize]
    [ProducesResponseType(typeof(DaemonHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DaemonHealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<DaemonHealthResponse> Health()
    {
        var healthEvent = new HealthCheckEvent { Context = HttpContext };
        var hook = _events.Emit(healthEvent);

        if (hook.IsCancelled && healthEvent.Response is DaemonHealthResponse cancelled)
            return _state.IsHealthy ? Ok(cancelled) : StatusCode(503, cancelled);

        if (hook.IsReplaced && hook.Replacement is DaemonHealthResponse replaced)
            return _state.IsHealthy ? Ok(replaced) : StatusCode(503, replaced);

        var snap = _metrics.Capture(_config.System.Data);
        var response = new DaemonHealthResponse(
            Status: _state.HealthStatus,
            Version: StartupBanner.Version,
            Uuid: _config.Uuid,
            UptimeSeconds: _state.UptimeSeconds,
            MaintenanceMode: _state.MaintenanceMode,
            PanelReachable: _state.PanelReachable,
            LastPanelError: _state.LastPanelError,
            WebspacesCount: _spaces.List().Count,
            DiskLimiter: _config.System.EffectiveDiskLimiterMode.ToString().ToLowerInvariant(),
            FusequotaAvailable: FuseQuotaLimiter.IsBinaryAvailable(_config.System),
            CpuPercent: snap.CpuPercent,
            MemoryUsedBytes: snap.MemoryUsedBytes,
            MemoryTotalBytes: snap.MemoryTotalBytes);

        return _state.IsHealthy ? Ok(response) : StatusCode(503, response);
    }

    /// <summary>Host utilization in panel-friendly flat shape.</summary>
    [HttpGet("utilization")]
    [Authorize]
    [ProducesResponseType(typeof(UtilizationEnvelope), StatusCodes.Status200OK)]
    public ActionResult<UtilizationEnvelope> Utilization()
    {
        // Two captures so CPU delta is meaningful on first request after idle.
        _ = _metrics.Capture(_config.System.Data);
        Thread.Sleep(120);
        var snap = _metrics.Capture(_config.System.Data);

        return Ok(new UtilizationEnvelope(new UtilizationBody(
            MemoryTotal: snap.MemoryTotalBytes,
            MemoryUsed: snap.MemoryUsedBytes,
            SwapTotal: snap.SwapTotalBytes,
            SwapUsed: snap.SwapUsedBytes,
            DiskTotal: snap.DiskTotalBytes,
            DiskUsed: snap.DiskUsedBytes,
            CpuPercent: snap.CpuPercent,
            LoadAverage1: snap.Load1,
            LoadAverage5: snap.Load5,
            LoadAverage15: snap.Load15,
            DiskDetails: [
                new UtilizationDiskDetail(
                    Path: snap.DiskPath,
                    Total: snap.DiskTotalBytes,
                    Used: snap.DiskUsedBytes),
            ])));
    }

    /// <summary>Calagopus-style nested stats.</summary>
    [HttpGet("stats")]
    [Authorize]
    [ProducesResponseType(typeof(StatsEnvelope), StatusCodes.Status200OK)]
    public ActionResult<StatsEnvelope> Stats()
    {
        _ = _metrics.Capture(_config.System.Data);
        Thread.Sleep(120);
        var snap = _metrics.Capture(_config.System.Data);

        return Ok(new StatsEnvelope(new StatsBody(
            Cpu: new StatsCpu(snap.CpuPercent, snap.CpuCount, snap.CpuModel),
            Memory: new StatsMemory(snap.MemoryUsedBytes, (ulong)GC.GetTotalMemory(false), snap.MemoryTotalBytes),
            Disk: new StatsDisk(snap.DiskUsedBytes, snap.DiskTotalBytes, 0, 0, 0, 0))));
    }

    /// <summary>Diagnostics / self-test snapshot.</summary>
    [HttpGet("diagnostics")]
    [Authorize]
    [ProducesResponseType(typeof(DiagnosticsResponse), StatusCodes.Status200OK)]
    public ActionResult<DiagnosticsResponse> Diagnostics()
    {
        var logger = HttpContext.RequestServices.GetRequiredService<Utils.Logger.Logger>();
        var live = StartupSelfTest.RunLive(_config, _spaces, logger, _diagnostics);

        var snap = _diagnostics.Snapshot();
        var host = _metrics.Capture(_config.System.Data);

        return Ok(new DiagnosticsResponse(
            Version: StartupBanner.Version,
            Uuid: _config.Uuid,
            UptimeSeconds: _state.UptimeSeconds,
            PanelReachable: _state.PanelReachable,
            LastPanelError: _state.LastPanelError,
            MaintenanceMode: _state.MaintenanceMode,
            BootCheckedAt: snap.BootCheckedAt,
            LiveCheckedAt: snap.LiveCheckedAt ?? DateTimeOffset.UtcNow,
            Checks: live,
            Host: new DiagnosticsHost(
                host.Architecture,
                host.Os,
                host.KernelVersion,
                host.CpuCount,
                host.CpuModel)));
    }

    /// <summary>Basic daemon identity (non-secret).</summary>
    [HttpGet("info")]
    [Authorize]
    [ProducesResponseType(typeof(SystemInfoResponse), StatusCodes.Status200OK)]
    public ActionResult<SystemInfoResponse> Info() =>
        Ok(new SystemInfoResponse(
            _config.AppName,
            _config.Uuid,
            _config.Debug,
            _config.System.Timezone,
            _config.System.User.Rootless.Enabled,
            _config.Plugins.Enabled));

    /// <summary>Loaded plugins (non-secret metadata).</summary>
    [HttpGet("plugins")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<PluginInfoResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PluginInfoResponse>> Plugins(
        [FromServices] Utils.Plugins.PluginManager pluginManager) =>
        Ok(pluginManager.Plugins.Select(p => new PluginInfoResponse(
            p.Instance.Metadata.Id,
            p.Instance.Metadata.Name,
            p.Instance.Metadata.Version,
            p.Instance.Metadata.Description)).ToList());
}

public sealed record SystemIdentityResponse(
    string Architecture,
    [property: JsonPropertyName("cpu_count")] int CpuCount,
    [property: JsonPropertyName("kernel_version")] string KernelVersion,
    string Os,
    string Version,
    SystemIdentityNested System);

public sealed record SystemIdentityNested(
    string Architecture,
    [property: JsonPropertyName("cpu_threads")] int CpuThreads,
    [property: JsonPropertyName("memory_bytes")] ulong MemoryBytes,
    [property: JsonPropertyName("kernel_version")] string KernelVersion,
    string Os,
    [property: JsonPropertyName("os_type")] string OsType);

public sealed record DaemonHealthResponse(
    string Status,
    string Version,
    Guid Uuid,
    [property: JsonPropertyName("uptime_seconds")] long UptimeSeconds,
    [property: JsonPropertyName("maintenance_mode")] bool MaintenanceMode = false,
    [property: JsonPropertyName("panel_reachable")] bool PanelReachable = true,
    [property: JsonPropertyName("last_panel_error")] string? LastPanelError = null,
    [property: JsonPropertyName("webspaces_count")] int WebspacesCount = 0,
    [property: JsonPropertyName("disk_limiter")] string DiskLimiter = "none",
    [property: JsonPropertyName("fusequota_available")] bool FusequotaAvailable = false,
    [property: JsonPropertyName("cpu_percent")] double CpuPercent = 0,
    [property: JsonPropertyName("memory_used_bytes")] ulong MemoryUsedBytes = 0,
    [property: JsonPropertyName("memory_total_bytes")] ulong MemoryTotalBytes = 0);

public sealed record UtilizationEnvelope(UtilizationBody Utilization);

public sealed record UtilizationBody(
    [property: JsonPropertyName("memory_total")] ulong MemoryTotal,
    [property: JsonPropertyName("memory_used")] ulong MemoryUsed,
    [property: JsonPropertyName("swap_total")] ulong SwapTotal,
    [property: JsonPropertyName("swap_used")] ulong SwapUsed,
    [property: JsonPropertyName("disk_total")] ulong DiskTotal,
    [property: JsonPropertyName("disk_used")] ulong DiskUsed,
    [property: JsonPropertyName("cpu_percent")] double CpuPercent,
    [property: JsonPropertyName("load_average1")] double LoadAverage1,
    [property: JsonPropertyName("load_average5")] double LoadAverage5,
    [property: JsonPropertyName("load_average15")] double LoadAverage15,
    [property: JsonPropertyName("disk_details")] IReadOnlyList<UtilizationDiskDetail> DiskDetails);

public sealed record UtilizationDiskDetail(
    string Path,
    ulong Total,
    ulong Used);

public sealed record StatsEnvelope(StatsBody Stats);

public sealed record StatsBody(
    StatsCpu Cpu,
    StatsMemory Memory,
    StatsDisk Disk);

public sealed record StatsCpu(double Used, int Threads, string Model);

public sealed record StatsMemory(
    ulong Used,
    [property: JsonPropertyName("used_process")] ulong UsedProcess,
    ulong Total);

public sealed record StatsDisk(
    ulong Used,
    ulong Total,
    ulong Read,
    [property: JsonPropertyName("reading_rate")] ulong ReadingRate,
    ulong Written,
    [property: JsonPropertyName("writing_rate")] ulong WritingRate);

public sealed record DiagnosticsResponse(
    string Version,
    Guid Uuid,
    [property: JsonPropertyName("uptime_seconds")] long UptimeSeconds,
    [property: JsonPropertyName("panel_reachable")] bool PanelReachable,
    [property: JsonPropertyName("last_panel_error")] string? LastPanelError,
    [property: JsonPropertyName("maintenance_mode")] bool MaintenanceMode,
    [property: JsonPropertyName("boot_checked_at")] DateTimeOffset? BootCheckedAt,
    [property: JsonPropertyName("live_checked_at")] DateTimeOffset LiveCheckedAt,
    IReadOnlyList<DiagnosticCheck> Checks,
    DiagnosticsHost Host);

public sealed record DiagnosticsHost(
    string Architecture,
    string Os,
    [property: JsonPropertyName("kernel_version")] string KernelVersion,
    [property: JsonPropertyName("cpu_count")] int CpuCount,
    [property: JsonPropertyName("cpu_model")] string CpuModel);

public sealed record SystemInfoResponse(
    string AppName,
    Guid Uuid,
    bool Debug,
    string Timezone,
    bool RootLess,
    bool PluginsEnabled);

public sealed record PluginInfoResponse(
    string Id,
    string Name,
    string Version,
    string? Description);
