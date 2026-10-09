using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Mail;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;
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
    private readonly IEventBus _events;

    public MailController(IEventBus? events = null) => _events = events.OrNoOp();

    private MailManager RequireManager(AppConfig config)
    {
        if (!MailProbe.ContainerRunning(config))
            throw new InvalidOperationException("Mail server is not running on this node.");
        return new MailManager(config, _events);
    }

    [HttpGet("probe")]
    public IActionResult Probe([FromServices] AppConfig config)
    {
        try
        {
            // Backend-aware: a mailcow on its own host has no local container to look for.
            if (MailProbe.StackRunning(config))
            {
                var mgr = new MailManager(config, _events);
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
            // Best-effort DKIM generation when the stack is up (local container or remote API)
            // so hints include keys.
            if (MailProbe.StackRunning(config))
            {
                try
                {
                    var mgr = new MailManager(config, _events);
                    mgr.EnsureDkim(domain);
                }
                catch
                {
                    // hints may still return MX/SPF without DKIM
                }
            }

            // mailcow keeps DKIM in redis, so the hints need the API; docker-mailserver
            // keeps the key as a file and gets null here.
            if (MailBackendKind.IsMailcow(config.System.Mail.Backend))
            {
                using var api = new MailcowApiClient(config);
                return Ok(MailDnsHelper.BuildHintsPayload(config, domain, api));
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

    [HttpGet("webmail")]
    public IActionResult WebmailStatus([FromServices] AppConfig config)
    {
        var hostname = WebmailSetup.NormalizeHostname(config.System.Mail.WebmailHostname ?? "");
        var running = WebmailProbe.ContainerRunning(config);
        var ssoReady = WebmailSetup.HasSsoSecret(config);
        return Ok(new
        {
            available = WebmailProbe.IsAvailable(config),
            container_running = running,
            http_reachable = WebmailProbe.HttpReachable(config),
            hostname,
            url = WebmailSetup.IsValidHostname(hostname) ? WebmailSetup.PublicUrl(hostname) : null,
            sso_ready = ssoReady,
            listen = $"127.0.0.1:{WebmailPaths.DefaultPort}",
            image = WebmailPaths.Image,
        });
    }

    [HttpPost("webmail/configure")]
    public IActionResult ConfigureWebmail(
        [FromBody] WebmailConfigureBody? body,
        [FromServices] AppConfig config,
        [FromServices] ReverseProxyManager proxy,
        [FromServices] WebSpaceStore spaces)
    {
        var hostname = WebmailSetup.NormalizeHostname(body?.Hostname ?? "");
        if (!WebmailSetup.IsValidHostname(hostname))
            return BadRequest(new { error = "hostname must be a valid FQDN (e.g. webmail.node.example.com)." });

        if (!WebmailProbe.ContainerRunning(config))
            return BadRequest(new { error = "webmail container is not running; install the webmail package first." });

        try
        {
            config.System.Mail.WebmailHostname = hostname;
            WebmailSetup.EnsureCustomFiles(config);
            var secret = WebmailSetup.EnsureSsoSecret(config);
            config.Save();
            proxy.Rebuild(spaces.List());

            return Ok(new
            {
                hostname,
                url = WebmailSetup.PublicUrl(hostname),
                sso_ready = true,
                sso_secret = secret,
            });
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

public sealed class WebmailConfigureBody
{
    public string? Hostname { get; set; }
}
