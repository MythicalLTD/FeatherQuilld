using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FeatherQuilld.Controllers;

/// <summary>Incoming cross-node WebSpace transfers (destination node).</summary>
[Tags("Transfers")]
[Authorize]
[EnableRateLimiting("expensive")]
[ApiController]
[Route("api/transfers")]
[RequestSizeLimit(1024L * 1024L * 1024L * 20L)] // 20 GiB
public sealed class TransfersController : ControllerBase
{
    private readonly WebSpaceTransferService _transfers;

    public TransfersController(WebSpaceTransferService transfers) => _transfers = transfers;

    /// <summary>Transfer progress for a WebSpace UUID (outgoing or incoming).</summary>
    [HttpGet("{uuid:guid}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Status(Guid uuid)
    {
        var state = _transfers.GetProgress(uuid);
        if (state is null)
            return NotFound(new { error = "No transfer state for this WebSpace." });
        return Ok(new
        {
            uuid = state.Uuid,
            phase = state.Phase.ToString().ToLowerInvariant(),
            direction = state.Direction,
            updated_at = state.UpdatedAt,
            message = state.Message,
        });
    }

    /// <summary>
    /// Accept a gzipped tar archive (raw body or multipart field <c>archive</c>).
    /// Headers: <c>X-WebSpace-Uuid</c>, optional <c>X-Start-On-Completion</c>=1.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Incoming(CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-WebSpace-Uuid", out var uuidHeader)
            || !Guid.TryParse(uuidHeader.ToString(), out var uuid)
            || uuid == Guid.Empty)
        {
            return BadRequest(new { error = "X-WebSpace-Uuid header is required." });
        }

        var start = Request.Headers.TryGetValue("X-Start-On-Completion", out var startHeader)
                    && startHeader.ToString() is "1" or "true" or "yes";

        Stream bodyStream;
        if (Request.HasFormContentType && Request.Form.Files.Count > 0)
        {
            var file = Request.Form.Files.GetFile("archive") ?? Request.Form.Files[0];
            bodyStream = file.OpenReadStream();
        }
        else
        {
            bodyStream = Request.Body;
        }

        try
        {
            await _transfers.IncomingAsync(uuid, bodyStream, start, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, new { uuid, ok = true });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
