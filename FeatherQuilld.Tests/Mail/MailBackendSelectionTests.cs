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

    [Fact]
    public void UnknownBackend_Message_PointsAtTheConfigKeyAndTheDocs()
    {
        var config = MakeConfig("mailcow-typo");
        var ex = Assert.Throws<InvalidOperationException>(() => MailBackendFactory.Create(config));
        Assert.Contains("system.mail.backend", ex.Message);
        Assert.Contains("docs/mail-backends.md", ex.Message);
    }

    [Fact]
    public void NotRunningHint_DockerMailserver_NamesTheHostPackage()
    {
        var config = MakeConfig(MailBackendKind.DockerMailserver);
        var hint = new DockerMailserverBackend(config).NotRunningHint();

        Assert.Contains("mailserver package", hint);
        Assert.Contains("docs/mail-backends.md", hint);
    }

    [Fact]
    public void NotRunningHint_Mailcow_OffersInstallAndRemoteSetup()
    {
        var hint = new MailcowBackend(MakeConfig(MailBackendKind.Mailcow)).NotRunningHint();

        Assert.Contains("/api/system/packages/mailcow/install", hint);
        Assert.Contains("mail.mailcow.url", hint);
        Assert.Contains("docs/mail-backends.md", hint);
    }

    [Fact]
    public void NotRunningHint_MailcowRemote_NamesTheApiKeyAndWhitelist()
    {
        var config = MakeConfig(MailBackendKind.Mailcow);
        config.System.Mail.Mailcow = new MailcowConfig { Url = "https://mail.example.com" };

        var hint = new MailcowBackend(config).NotRunningHint();

        Assert.Contains("mail.example.com", hint);
        Assert.Contains("api_key", hint);
        Assert.Contains("API_ALLOW_FROM", hint);
    }

    [Fact]
    public void MailManager_NotRunning_AppendsTheBackendHint()
    {
        var config = MakeConfig(MailBackendKind.Mailcow);
        var ex = Assert.Throws<InvalidOperationException>(
            () => new MailManager(config, events: null, backend: new StubNotRunningBackend()));

        Assert.Contains("stub-stack mail server is not running", ex.Message);
        Assert.Contains("install the stub package", ex.Message);
    }

    /// <summary>Minimal IMailBackend so the guard message can be asserted without docker.</summary>
    private sealed class StubNotRunningBackend : IMailBackend
    {
        public string Kind => "stub";
        public string DisplayName => "stub-stack";
        public bool IsRunning() => false;
        public string NotRunningHint() => "install the stub package (docs/mail-backends.md)";
        public object ProbeStatus() => new { available = false };
        public IReadOnlyList<string> ListDomains() => [];
        public void AddDomain(string domain) { }
        public void RemoveDomain(string domain) { }
        public bool EnsureDkim(string domain, int maxAttempts = 6, int delayMs = 1000) => false;
        public void CreateMailbox(string email, string password) { }
        public void DeleteMailbox(string email) { }
        public void UpdateMailboxPassword(string email, string password) { }
        public void SetMailboxEnabled(string email, bool enabled) { }
        public void AddAlias(string source, string destination) { }
        public void DeleteAlias(string source, string? destination) { }
        public bool GetSpamFilterEnabled(string email) => false;
        public void SetSpamFilterEnabled(string email, bool enabled) { }
        public void SetAutorespond(string email, bool enabled, string subject, string body) { }
    }
}
