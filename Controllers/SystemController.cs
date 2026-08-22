using FeatherQuilld.Plugins.Sdk.Events;
using FeatherQuilld.Utils.Config;
using Microsoft.AspNetCore.Mvc;

namespace FeatherQuilld.Controllers;

/// <summary>
/// Process health and identity endpoints.
/// </summary>
[Tags("System")]
public sealed class SystemController : ApiControllerBase
{
    private readonly Config _config;
    private readonly IEventBus _events;

    public SystemController(Config config, IEventBus events)
    {
        _config = config;
        _events = events;
    }

    /// <summary>Liveness probe.</summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Health()
    {
        var healthEvent = new HealthCheckEvent { Context = HttpContext };
        var hook = _events.Emit(healthEvent);

        if (hook.IsCancelled && healthEvent.Response is HealthResponse cancelled)
            return Ok(cancelled);

        if (hook.IsReplaced && hook.Replacement is HealthResponse replaced)
            return Ok(replaced);

        return Ok(new HealthResponse("ok", _config.AppName, DateTimeOffset.UtcNow));
    }

    /// <summary>Basic daemon identity (non-secret).</summary>
    [HttpGet("info")]
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
    [ProducesResponseType(typeof(IReadOnlyList<PluginInfoResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PluginInfoResponse>> Plugins(
        [FromServices] Utils.Plugins.PluginManager pluginManager) =>
        Ok(pluginManager.Plugins.Select(p => new PluginInfoResponse(
            p.Instance.Metadata.Id,
            p.Instance.Metadata.Name,
            p.Instance.Metadata.Version,
            p.Instance.Metadata.Description)).ToList());
}

public sealed record HealthResponse(string Status, string AppName, DateTimeOffset Timestamp);

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
