using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

/// <summary>
/// Feature coverage for the selectable mail backend
/// (<c>system.mail.backend: docker-mailserver | mailcow</c>). The default must stay
/// docker-mailserver so existing installations behave exactly as before, and a
/// typo must never silently switch the managed stack.
/// </summary>
public class MailBackendSelectionTests
{
    private static AppConfig MakeConfig(string backend, string? root = null) => new()
    {
        System = new SystemConfig
        {
            RootDirectory = root ?? Path.Combine(Path.GetTempPath(), "fq-backend-" + Guid.NewGuid().ToString("N")),
            Mail = new MailConfig { Backend = backend },
        },
    };

    [Fact]
    public void DefaultConfig_UsesDockerMailserver()
    {
        var config = MakeConfig("docker-mailserver");
        Assert.Equal(MailBackendKind.DockerMailserver, MailBackendFactory.Create(config).Kind);
    }

    [Fact]
    public void EmptyBackend_FallsBackToDockerMailserver()
    {
        var config = MakeConfig("");
        Assert.Equal(MailBackendKind.DockerMailserver, MailBackendFactory.Create(config).Kind);
    }

    [Theory]
    [InlineData("mailcow")]
    [InlineData("MAILCOW")]
    [InlineData("mailcow-dockerized")]
    public void MailcowAliases_SelectMailcowBackend(string value)
    {
        var config = MakeConfig(value);
        var backend = MailBackendFactory.Create(config);
        Assert.Equal(MailBackendKind.Mailcow, backend.Kind);
        Assert.IsType<MailcowBackend>(backend);
    }

    [Fact]
    public void UnknownBackend_IsRejectedInsteadOfSilentlySwitching()
    {
        var config = MakeConfig("mailcow-typo");
        var ex = Assert.Throws<InvalidOperationException>(() => MailBackendFactory.Create(config));
        Assert.Contains("mailcow-typo", ex.Message);
        Assert.Contains(MailBackendKind.Mailcow, ex.Message);
    }

    [Fact]
    public void Normalize_IsLenientForDiagnostics()
    {
        Assert.Equal(MailBackendKind.DockerMailserver, MailBackendKind.Normalize("nonsense"));
        Assert.True(MailBackendKind.IsMailcow(" mailcow "));
        Assert.False(MailBackendKind.IsMailcow("docker-mailserver"));
        Assert.True(MailBackendKind.IsKnown(""));
        Assert.False(MailBackendKind.IsKnown("nonsense"));
    }

    [Fact]
    public void MailcowPaths_SitNextToTheDockerMailserverPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-backend-paths-" + Guid.NewGuid().ToString("N"));
        var config = MakeConfig("mailcow", root);
        config.System.Mail.DataPath = Path.Combine(root, "mail");

        Assert.Equal(Path.Combine(root, "mail", "mailcow"), MailcowPaths.Root(config));
        Assert.Equal(Path.Combine(root, "mail", "mailcow", "docker-compose.yml"), MailcowPaths.ComposeFile(config));
        Assert.Equal(Path.Combine(root, "mail", "mailcow", "data", "dkim", "example.com", "mail.txt"),
            MailcowPaths.DkimKeyFile(config, "Example.COM", "mail"));

        // An explicit mailcow path wins over the derived one.
        config.System.Mail.Mailcow.Path = "/srv/mailcow";
        Assert.Equal("/srv/mailcow", MailcowPaths.Root(config));
    }
}
