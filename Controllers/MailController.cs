using FeatherQuilld.Utils.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Controllers;

[Tags("Mail")]
[Authorize]
[ApiController]
[Route("api/mail")]
[Produces("application/json")]
public sealed class MailController : ControllerBase
{
    private static MailManager RequireManager(AppConfig config)
    {
        if (!MailProbe.ContainerRunning(config))
            throw new InvalidOperationException("Mail server is not running on this node.");
        return new MailManager(config);
    }

    [HttpGet("probe")]
    public IActionResult Probe([FromServices] AppConfig config)
    {
        try
        {
            if (MailProbe.ContainerRunning(config))
            {
                var mgr = new MailManager(config);
                return Ok(mgr.ProbeStatus());
            }
        }
        catch
        {
            // fall through
        }

        return Ok(new
        {
            available = MailProbe.IsAvailable(config),
            container = MailPaths.ContainerName,
            docker = MailProbe.DockerOnPath(),
            smtp_port = config.System.Mail.SmtpPort,
            imap_port = config.System.Mail.ImapPort,
            port_25_open = MailProbe.PortOpen(25),
            submission_open = MailProbe.SmtpReachable(config),
            imap_open = MailProbe.ImapReachable(config),
            deliverability_hint = MailProbe.PortOpen(25)
                ? null
                : "SMTP port 25 is not listening inbound MX and many providers require it; also set PTR/rDNS for outbound.",
        });
    }

    [HttpGet("domains")]
    public IActionResult ListDomains([FromServices] AppConfig config)
    {
        try
        {
            var mgr = RequireManager(config);
            return Ok(new { domains = mgr.ListDomains() });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("domains")]
    public IActionResult AddDomain([FromBody] MailDomainBody? body, [FromServices] AppConfig config)
    {
        var name = body?.Name?.Trim() ?? "";
        if (name.Length == 0)
            return BadRequest(new { error = "name is required." });
        try
        {
            var mgr = RequireManager(config);
            mgr.AddDomain(name);
            return Ok(new { ok = true, name = name.Trim().ToLowerInvariant() });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("domains/{name}")]
    public IActionResult RemoveDomain(string name, [FromServices] AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "name is required." });
        try
        {
            var mgr = RequireManager(config);
            mgr.RemoveDomain(name);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("provision")]
    public IActionResult Provision([FromBody] Dictionary<string, object?>? body, [FromServices] AppConfig config)
    {
        if (body is null || body.Count == 0)
            return BadRequest(new { error = "payload is required." });
        try
        {
            var mgr = RequireManager(config);
            return Ok(mgr.Provision(body));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("dns-hints/{domain}")]
    public IActionResult DnsHints(string domain, [FromServices] AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest(new { error = "domain is required." });
        try
        {
            // Best-effort DKIM generation when container is up so hints include keys.
            if (MailProbe.ContainerRunning(config))
            {
                try
                {
                    var mgr = new MailManager(config);
                    mgr.EnsureDkim(domain);
                }
                catch
                {
                    // hints may still return MX/SPF without DKIM
                }
            }

            return Ok(MailDnsHelper.BuildHintsPayload(config, domain));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("mailboxes/{email}/spam")]
    public IActionResult GetSpamFilter(string email, [FromServices] AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "email is required." });
        try
        {
            var mgr = RequireManager(config);
            return Ok(new { email = email.Trim().ToLowerInvariant(), enabled = mgr.GetSpamFilterEnabled(email) });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("lists")]
    public IActionResult ListMailingLists([FromQuery] string? domain, [FromServices] AppConfig config)
    {
        try
        {
            var mgr = RequireManager(config);
            return Ok(new { lists = mgr.ListMailingLists(domain) });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("deliverability")]
    public IActionResult Deliverability(
        [FromQuery] string domain,
        [FromQuery] string? public_ip,
        [FromServices] AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest(new { error = "domain is required." });

        try
        {
            return Ok(MailDeliverabilityHelper.BuildPayload(config, domain, public_ip));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed class MailDomainBody
{
    public string? Name { get; set; }
}
