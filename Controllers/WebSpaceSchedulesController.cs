using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeatherQuilld.Controllers;

[Tags("Schedules")]
[Authorize]
[ApiController]
[Route("api/webspaces/{uuid}/schedules")]
public sealed class WebSpaceSchedulesController(WebSpaceScheduleManager scheduleManager) : ControllerBase
{
    /// <summary>Pull schedule definitions from the panel and apply them locally.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(string uuid, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(uuid, out var guid))
        {
            return BadRequest(new { error = "Invalid webspace uuid" });
        }

        await scheduleManager.SyncWebSpaceFromPanelAsync(guid, cancellationToken).ConfigureAwait(false);
        return Ok(new { synced = true });
    }

    /// <summary>Trigger a schedule task immediately.</summary>
    [HttpPost("{scheduleId:int}/trigger")]
    public async Task<IActionResult> Trigger(string uuid, int scheduleId, CancellationToken cancellationToken)
    {
        var ok = await scheduleManager.TriggerAsync(uuid, scheduleId, cancellationToken).ConfigureAwait(false);
        return ok ? Accepted() : NotFound();
    }

    /// <summary>Abort a running scheduled task for this WebSpace.</summary>
    [HttpPost("abort")]
    public IActionResult Abort(string uuid)
    {
        var aborted = scheduleManager.Abort(uuid);
        return Ok(new { aborted, running = scheduleManager.IsRunning(uuid) });
    }
}
