using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeatherQuilld.Controllers;

[Tags("Auth")]
[Authorize]
[ApiController]
[Route("api")]
[Produces("application/json")]
public sealed class DeauthorizeController : ControllerBase
{
    private readonly WebSpaceUserAccessService _access;

    public DeauthorizeController(WebSpaceUserAccessService access) => _access = access;

    public sealed class DeauthorizeBody
    {
        public Guid User { get; set; }
        public List<Guid>? Webspaces { get; set; }
        public List<Guid>? Servers { get; set; }
    }

    public sealed class PermissionsBody
    {
        public Guid User { get; set; }
        public Guid Webspace { get; set; }
        public List<string>? Permissions { get; set; }
    }

    [HttpPost("deauthorize-user")]
    public IActionResult Deauthorize([FromBody] DeauthorizeBody body)
    {
        if (body.User == Guid.Empty)
            return BadRequest(new { error = "user is required." });

        var spaces = (body.Webspaces ?? body.Servers ?? [])
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (spaces.Count == 0)
            return BadRequest(new { error = "webspaces (or servers) must include at least one UUID." });

        _access.Deauthorize(body.User, spaces);
        return Ok(new { ok = true });
    }

    [HttpPost("webspaces/{uuid:guid}/ws/permissions")]
    public IActionResult PushPermissions(Guid uuid, [FromBody] PermissionsBody body)
    {
        if (body.User == Guid.Empty)
            return BadRequest(new { error = "user is required." });

        var perms = (body.Permissions ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        _access.SetPermissions(body.User, uuid, perms);
        return Ok(new { ok = true });
    }
}
