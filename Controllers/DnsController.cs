using FeatherQuilld.Utils.Dns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Controllers;

[Tags("DNS")]
[Authorize]
[ApiController]
[Route("api/dns")]
[Produces("application/json")]
public sealed class DnsController : ControllerBase
{
    private static PowerDnsManager RequireManager(AppConfig config)
    {
        if (!PowerDnsProbe.IsAvailable(config))
            throw new InvalidOperationException("PowerDNS is not available on this node.");
        return new PowerDnsManager(config);
    }

    [HttpGet("probe")]
    public IActionResult Probe([FromServices] AppConfig config)
    {
        try
        {
            var mgr = new PowerDnsManager(config);
            return Ok(mgr.ProbeStatus());
        }
        catch
        {
            return Ok(new
            {
                available = PowerDnsProbe.IsAvailable(config),
                binary = PowerDnsProbe.ResolveBinary(),
                api_url = config.System.Dns.PowerDnsApiUrl,
            });
        }
    }

    [HttpGet("zones")]
    public IActionResult ListZones([FromServices] AppConfig config)
    {
        try
        {
            var mgr = RequireManager(config);
            return Ok(new { zones = mgr.ListZones() });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("zones")]
    public IActionResult CreateZone([FromBody] CreateDnsZoneBody? body, [FromServices] AppConfig config)
    {
        var name = body?.Name?.Trim() ?? "";
        if (name.Length == 0)
            return BadRequest(new { error = "name is required." });
        try
        {
            var mgr = RequireManager(config);
            var id = mgr.CreateZone(name, body?.NodeIp?.Trim());
            var nameservers = PowerDnsManager.DefaultNameservers(name);
            return Ok(new { id, name = id, nameservers, node_ip = body?.NodeIp?.Trim() });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("zones/{zone}/records")]
    public IActionResult ListRecords(
        string zone,
        [FromQuery] string? type,
        [FromQuery] string? name,
        [FromQuery] int page = 1,
        [FromQuery] int per_page = 100,
        [FromServices] AppConfig config = null!)
    {
        try
        {
            var mgr = RequireManager(config);
            return Ok(mgr.ListRecords(zone, type, name, page, per_page));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("zones/{zone}/records")]
    public IActionResult CreateRecord(string zone, [FromBody] Dictionary<string, object?>? body, [FromServices] AppConfig config)
    {
        if (body is null || body.Count == 0)
            return BadRequest(new { error = "payload is required." });
        try
        {
            var mgr = RequireManager(config);
            return Ok(mgr.CreateRecord(zone, body));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPatch("zones/{zone}/records/{recordId}")]
    public IActionResult UpdateRecord(
        string zone,
        string recordId,
        [FromBody] Dictionary<string, object?>? body,
        [FromServices] AppConfig config)
    {
        if (body is null || body.Count == 0)
            return BadRequest(new { error = "payload is required." });
        try
        {
            var mgr = RequireManager(config);
            return Ok(mgr.UpdateRecord(zone, recordId, body));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("zones/{zone}/records/{recordId}")]
    public IActionResult DeleteRecord(string zone, string recordId, [FromServices] AppConfig config)
    {
        try
        {
            var mgr = RequireManager(config);
            mgr.DeleteRecord(zone, recordId);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("zones/{zone}/txt")]
    public IActionResult UpsertTxt(string zone, [FromBody] DnsTxtBody? body, [FromServices] AppConfig config)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "name is required." });
        if (string.IsNullOrWhiteSpace(body.Content))
            return BadRequest(new { error = "content is required." });
        try
        {
            var mgr = RequireManager(config);
            return Ok(mgr.CreateTxtRecord(zone, body.Name, body.Content, body.Ttl ?? 120));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("zones/{zone}/txt")]
    public IActionResult DeleteTxt(string zone, [FromQuery] string name, [FromQuery] string? content, [FromServices] AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "name is required." });
        try
        {
            var mgr = RequireManager(config);
            return Ok(mgr.DeleteTxtRecords(zone, name, content));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class CreateDnsZoneBody
{
    public string? Name { get; set; }
    public string? NodeIp { get; set; }
}

public sealed class DnsTxtBody
{
    public string? Name { get; set; }
    public string? Content { get; set; }
    public int? Ttl { get; set; }
}
