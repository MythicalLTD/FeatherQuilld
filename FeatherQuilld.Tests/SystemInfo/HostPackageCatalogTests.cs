using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.SystemInfo;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.SystemInfo;

/// <summary>
/// The panel's package manager renders whatever <c>GET /api/system/packages</c> returns, so the
/// mail packages have to explain themselves: an operator who installs mailcow from the panel needs
/// to know that the backend switch lives in the node config and where the guide is.
/// </summary>
public class HostPackageCatalogTests
{
    private static AppConfig MakeConfig() => new()
    {
        System = new SystemConfig
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "fq-packages-" + Guid.NewGuid().ToString("N")),
        },
    };

    [Fact]
    public void MailPackages_CarryADescriptionAndTheGuideLink()
    {
        var packages = new HostPackageManager(config: MakeConfig()).List();

        foreach (var id in new[] { "mailserver", "mailcow", "webmail" })
        {
            var package = Assert.Single(packages, p => p.Id == id);

            Assert.False(string.IsNullOrWhiteSpace(package.Description), $"{id} has no description");
            Assert.Contains("docs/mail-backends.md", package.DocsUrl);
        }
    }

    [Fact]
    public void MailcowDescription_PointsAtTheBackendSwitch()
    {
        var mailcow = Assert.Single(
            new HostPackageManager(config: MakeConfig()).List(),
            p => p.Id == "mailcow");

        Assert.Contains("system.mail.backend: mailcow", mailcow.Description);
        Assert.Contains("config overrides", mailcow.Description);
    }
}
