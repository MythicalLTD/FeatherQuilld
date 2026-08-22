using FeatherQuilld.Plugins.Sdk.Events;
using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Controllers;

/// <summary>
/// Process health and identity endpoints.
/// </summary>
[Tags("System")]
public sealed class SystemController : ApiControllerBase
{
    private readonly Config _config;
    private readonly DaemonState _state;
    private readonly IEventBus _events;

    public SystemController(Config config, DaemonState state, IEventBus events)
    {
        _config = config;
        _state = state;
        _events = events;
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

        var response = new DaemonHealthResponse(
            _state.HealthStatus,
            StartupBanner.Version,
            _config.Uuid,
            _state.UptimeSeconds);

        return _state.IsHealthy ? Ok(response) : StatusCode(503, response);
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

public sealed record DaemonHealthResponse(
    string Status,
    string Version,
    Guid Uuid,
    [property: JsonPropertyName("uptime_seconds")] long UptimeSeconds);

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
