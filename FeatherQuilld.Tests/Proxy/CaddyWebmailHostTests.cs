using System.Linq;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Proxy;

public class CaddyWebmailHostTests
{
    private static ReverseProxyManager Manager(string root, bool webmailRunning, string webmailHostname = "mail.example.com")
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = root,
                Data = Path.Combine(root, "data"),
                Proxy = new ProxyConfig { Enabled = true, Provider = "caddy", AcmeEmail = "ops@example.com" },
                Mail = new MailConfig { WebmailHostname = webmailHostname },
            },
        };
        return new ReverseProxyManager(config) { WebmailRunning = () => webmailRunning };
    }

    private static WebSpace Space(params (string domain, string type)[] routes) => new()
    {
        Uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Domains = routes.Select(r => r.domain).ToList(),
        DomainRoutes = routes.Select(r => new WebSpaceDomainRoute { Domain = r.domain, Type = r.type }).ToList(),
        Ssl = true,
        BackendPort = 20123,
        DocumentRoot = "public",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static int SiteAddressCount(string caddyfile, string address) =>
        caddyfile.Split('\n').Count(l => string.Equals(l.Trim(), address, StringComparison.OrdinalIgnoreCase));

    private static string Build(bool webmailRunning, params (string domain, string type)[] routes)
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-webmail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            return Manager(root, webmailRunning).BuildConfig([Space(routes)]);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void WebSpaceAliasOnTheWebmailHost_DoesNotProduceASecondSiteBlock()
    {
        var caddyfile = Build(true, ("www.example.com", "primary"), ("mail.example.com", "alias"));

        // a second block with the same address makes Caddy refuse the file: "ambiguous site definition"
        Assert.Equal(1, SiteAddressCount(caddyfile, "mail.example.com"));
        Assert.Equal(1, SiteAddressCount(caddyfile, "www.example.com"));
        Assert.Contains("reverse_proxy 127.0.0.1:" + FeatherQuilld.Utils.Mail.WebmailPaths.DefaultPort, caddyfile);
    }

    [Fact]
    public void WebmailKeepsTheHost_EvenWhenAWebSpaceListsItWithDifferentCaseOrAsRedirect()
    {
        var caddyfile = Build(true, ("www.example.com", "primary"), ("MAIL.Example.com", "alias"), ("mail.example.com", "redirect"));

        Assert.Equal(1, SiteAddressCount(caddyfile, "mail.example.com"));
        Assert.DoesNotContain("redir ", caddyfile);
    }

    [Fact]
    public void WebSpaceThatOnlyHasTheWebmailHost_IsSkippedWithoutBreakingTheFile()
    {
        var caddyfile = Build(true, ("mail.example.com", "primary"));

        Assert.Equal(1, SiteAddressCount(caddyfile, "mail.example.com"));
        Assert.DoesNotContain("20123", caddyfile);
    }

    [Fact]
    public void WithoutAConflict_BothBlocksArePresent()
    {
        var caddyfile = Build(true, ("www.example.com", "primary"), ("shop.example.com", "alias"));

        Assert.Equal(1, SiteAddressCount(caddyfile, "mail.example.com"));
        Assert.Equal(1, SiteAddressCount(caddyfile, "www.example.com"));
        Assert.Equal(1, SiteAddressCount(caddyfile, "shop.example.com"));
    }

    [Fact]
    public void WebmailNotRunning_WebSpaceKeepsItsDomain()
    {
        var caddyfile = Build(false, ("www.example.com", "primary"), ("mail.example.com", "alias"));

        Assert.Equal(1, SiteAddressCount(caddyfile, "mail.example.com"));
        Assert.Contains("20123", caddyfile);
        Assert.DoesNotContain("reverse_proxy 127.0.0.1:" + FeatherQuilld.Utils.Mail.WebmailPaths.DefaultPort, caddyfile);
    }
}
