using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.SystemInfo;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Disk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Controllers;

/// <summary>Wings-style system / health / stats / diagnostics endpoints.</summary>
[Tags("System")]
public sealed class SystemController : ApiControllerBase
{
    private static readonly JsonSerializerOptions WsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

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

    /// <summary>Live package install/remove output (bearer via Authorization header or <c>?token=</c>).</summary>
    [Authorize]
    [HttpGet("packages/ws")]
    public async Task PackageWs(
        [FromServices] SystemPackageWsHub wsHub,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsJsonAsync(
                new { error = "Expected WebSocket upgrade." },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var socketId = wsHub.Register(socket);

        try
        {
            await SendWsEventAsync(socket, "auth success", [], cancellationToken).ConfigureAwait(false);
            await wsHub.ReplayActiveOperationsAsync(socketId, socket, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // expected during host shutdown (includes connection abort on stop)
        }
        catch (WebSocketException)
        {
            // client disconnected
        }
        finally
        {
            wsHub.Unregister(socketId);
        }

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignore close races during shutdown
            }
        }
    }

    /// <summary>List installable host packages (reverse proxy, Docker).</summary>
    [HttpGet("packages")]
    [Authorize]
    [ProducesResponseType(typeof(HostPackagesResponse), StatusCodes.Status200OK)]
    public ActionResult<HostPackagesResponse> Packages(
        [FromServices] HostPackageManager packages)
    {
        var manager = OperatingSystem.IsLinux() && global::System.IO.File.Exists("/usr/bin/apt-get") ? "apt"
            : OperatingSystem.IsLinux() && (global::System.IO.File.Exists("/usr/bin/dnf") || global::System.IO.File.Exists("/usr/bin/yum")) ? "dnf"
            : null;
        var listed = packages.List();
        var activeProxy = listed.FirstOrDefault(p => p.Category == "reverse_proxy" && p.Installed)?.Id;

        return Ok(new HostPackagesResponse(manager, listed, activeProxy));
    }

    /// <summary>Install a supported host package.</summary>
    [HttpPost("packages/{packageId}/install")]
    [Authorize]
    [ProducesResponseType(typeof(HostPackageOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HostPackageOperationResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HostPackageOperationResponse>> InstallPackage(
        string packageId,
        [FromServices] HostPackageManager packages,
        [FromServices] Utils.Logger.Logger logger,
        CancellationToken cancellationToken)
    {
        var result = await packages.InstallAsync(packageId, logger, cancellationToken).ConfigureAwait(false);
        var body = new HostPackageOperationResponse(result.Success, result.Message, result.Output, packages.List());
        return result.Success ? Ok(body) : BadRequest(body);
    }

    /// <summary>Remove a supported host package.</summary>
    [HttpPost("packages/{packageId}/remove")]
    [Authorize]
    [ProducesResponseType(typeof(HostPackageOperationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HostPackageOperationResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HostPackageOperationResponse>> RemovePackage(
        string packageId,
        [FromQuery] bool purge_config = false,
        [FromServices] HostPackageManager packages = null!,
        [FromServices] Utils.Logger.Logger logger = null!,
        CancellationToken cancellationToken = default)
    {
        var result = await packages.RemoveAsync(packageId, purge_config, logger, cancellationToken).ConfigureAwait(false);
        var body = new HostPackageOperationResponse(result.Success, result.Message, result.Output, packages.List());
        return result.Success ? Ok(body) : BadRequest(body);
    }

    /// <summary>Compare installed version with upstream (GitHub latest).</summary>
    [HttpGet("version-status")]
    [Authorize]
    [ProducesResponseType(typeof(DaemonVersionStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DaemonVersionStatusResponse>> VersionStatus(CancellationToken cancellationToken)
    {
        var current = StartupBanner.Version.Trim().TrimStart('v');
        string? latest = null;
        string? githubError = null;
        const string owner = "mythicalltd";
        const string repo = "featherquilld";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("FeatherQuilld", StartupBanner.Version));
            using var response = await http.GetAsync(
                $"https://api.github.com/repos/{owner}/{repo}/releases/latest",
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var json = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken).ConfigureAwait(false);
            latest = doc.RootElement.GetProperty("tag_name").GetString()?.Trim().TrimStart('v');
        }
        catch (Exception ex)
        {
            githubError = ex.Message;
        }

        var updateAvailable = latest is not null && string.CompareOrdinal(current, latest) < 0;
        return Ok(new DaemonVersionStatusResponse(
            CurrentVersion: StartupBanner.Version,
            LatestVersion: latest,
            IsUpToDate: latest is null || !updateAvailable,
            UpdateAvailable: updateAvailable,
            GithubOwner: owner,
            GithubRepo: repo,
            GithubError: githubError));
    }

    /// <summary>Download and replace the running FeatherQuilld binary.</summary>
    [HttpPost("self-update")]
    [Authorize]
    [ProducesResponseType(typeof(SelfUpdateResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(SelfUpdateResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SelfUpdateResponse>> SelfUpdate(
        [FromBody] SelfUpdateRequestBody? body,
        [FromServices] Utils.Logger.Logger logger,
        CancellationToken cancellationToken)
    {
        var request = new DaemonSelfUpdater.SelfUpdateRequest(
            Source: body?.Source ?? "github",
            RepoOwner: body?.RepoOwner,
            RepoName: body?.RepoName,
            Version: body?.Version,
            Url: body?.Url,
            Sha256: body?.Sha256,
            Force: body?.Force ?? false,
            DisableChecksum: body?.DisableChecksum ?? false);

        var result = await DaemonSelfUpdater.ApplyAsync(request, logger, cancellationToken).ConfigureAwait(false);
        var response = new SelfUpdateResponse(result.Success, result.Message, result.RestartScheduled);
        return result.Success ? Accepted(response) : BadRequest(response);
    }

    private static async Task SendWsEventAsync(
        WebSocket socket,
        string eventName,
        string[] args,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { Event = eventName, Args = args }, WsJson);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>List daemon log files (<c>latest.log</c> and archived <c>*.log.gz</c>).</summary>
    [HttpGet("logs")]
    [Authorize]
    [ProducesResponseType(typeof(SystemLogsListResponse), StatusCodes.Status200OK)]
    public ActionResult<SystemLogsListResponse> ListLogs(
        [FromServices] Utils.Logger.Logger logger)
    {
        var files = SystemLogReader.ListFiles(logger.LogsDirectory)
            .Select(f => new SystemLogFileResponse(
                f.Name,
                f.SizeBytes,
                f.ModifiedAt,
                f.Compressed))
            .ToList();

        return Ok(new SystemLogsListResponse(logger.LogsDirectory, files));
    }

    /// <summary>Tail lines from a daemon log file.</summary>
    [HttpGet("logs/{fileName}")]
    [Authorize]
    [ProducesResponseType(typeof(SystemLogContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SystemLogContentResponse> ReadLog(
        string fileName,
        [FromQuery] int lines = 200,
        [FromServices] Utils.Logger.Logger logger = null!)
    {
        try
        {
            var content = SystemLogReader.ReadTail(logger.LogsDirectory, fileName, lines);
            return Ok(new SystemLogContentResponse(fileName, Math.Clamp(lines, 1, 5000), content));
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Log file not found." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
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
    [property: JsonPropertyName("disk_limiter")] string DiskLimiter = "fusequota",
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

public sealed record SystemLogsListResponse(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("files")] IReadOnlyList<SystemLogFileResponse> Files);

public sealed record SystemLogFileResponse(
    string Name,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("modified_at")] DateTimeOffset ModifiedAt,
    bool Compressed);

public sealed record SystemLogContentResponse(
    string File,
    int Lines,
    string Content);

public sealed record HostPackageOperationResponse(
    bool Success,
    string Message,
    string? Output,
    IReadOnlyList<HostPackageStatus> Packages);

public sealed record DaemonVersionStatusResponse(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("latest_version")] string? LatestVersion,
    [property: JsonPropertyName("is_up_to_date")] bool IsUpToDate,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("github_owner")] string GithubOwner,
    [property: JsonPropertyName("github_repo")] string GithubRepo,
    [property: JsonPropertyName("github_error")] string? GithubError);

public sealed record SelfUpdateRequestBody(
    string? Source = null,
    [property: JsonPropertyName("repo_owner")] string? RepoOwner = null,
    [property: JsonPropertyName("repo_name")] string? RepoName = null,
    string? Version = null,
    string? Url = null,
    string? Sha256 = null,
    bool Force = false,
    [property: JsonPropertyName("disable_checksum")] bool DisableChecksum = false);

public sealed record SelfUpdateResponse(
    bool Success,
    string Message,
    [property: JsonPropertyName("restart_scheduled")] bool RestartScheduled);
