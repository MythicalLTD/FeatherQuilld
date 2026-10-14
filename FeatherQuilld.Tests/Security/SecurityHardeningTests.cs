using FeatherQuilld.Middleware;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Security;

/// <summary>
/// Regression tests for the hardenings from the 2026-10 security review:
/// token masking in the request log, path-traversal guards for mail/spam file
/// names and the Apache document-root allowlist.
/// </summary>
public class SecurityHardeningTests : IDisposable
{
    private readonly string _root;

    public SecurityHardeningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fq-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private AppConfig Config() => new()
    {
        System = new SystemConfig
        {
            RootDirectory = _root,
            Mail = new MailConfig { DataPath = Path.Combine(_root, "mail") },
        },
    };

    [Theory]
    [InlineData("/api/x?token=secret&y=1", "/api/x?token=***&y=1")]
    [InlineData("/api/x?a=1&token=secret", "/api/x?a=1&token=***")]
    [InlineData("/api/x?token=", "/api/x?token=***")]
    [InlineData("/api/x?tokens=secret", "/api/x?tokens=secret")]
    [InlineData("/api/x?a=1", "/api/x?a=1")]
    public void MaskSecrets_hides_token_but_keeps_the_rest(string input, string expected) =>
        Assert.Equal(expected, RequestResponseLoggingMiddleware.MaskSecrets(input));

    [Theory]
    [InlineData("public/site", "public/site")]
    [InlineData("public", "public")]
    [InlineData("", "")]
    [InlineData("../etc", "")]
    [InlineData("public\n  ServerName evil.example.com", "")]
    [InlineData("public\"</Directory>", "")]
    [InlineData("public/../etc", "")]
    public void SanitizeRelativeDocumentRoot_accepts_only_plain_relative_paths(string input, string expected) =>
        Assert.Equal(expected, WebSpaceSiteFiles.SanitizeRelativeDocumentRoot(input));

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("first.last+tag@sub.example.com", true)]
    [InlineData("user@localhost", true)]
    [InlineData("../evil@example.com", false)]
    [InlineData("user@../../etc", false)]
    [InlineData("a/b@example.com", false)]
    [InlineData("..@example.com", false)]
    [InlineData("mail.txt", false)]
    [InlineData("", false)]
    public void MailAddressRules_only_accept_plain_addresses(string address, bool expected) =>
        Assert.Equal(expected, MailAddressRules.IsValidAddress(address));

    [Fact]
    public void ConfigSievePath_rejects_traversal_but_keeps_valid_addresses()
    {
        var config = Config();

        Assert.Equal(string.Empty, MailVacationHelper.ConfigSievePath(config, "../../escape"));
        Assert.Equal(string.Empty, MailVacationHelper.ConfigSievePath(config, "not-an-address"));

        MailVacationHelper.WriteAutorespond(config, "../evil", "Away", "nope");
        // Old behaviour would have written this outside the config dir.
        Assert.False(File.Exists(Path.Combine(_root, "mail", "evil.dovecot.sieve")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.dovecot.sieve")));

        MailVacationHelper.WriteAutorespond(config, "alice@example.com", "Away", "Back soon");
        var path = MailVacationHelper.ConfigSievePath(config, "alice@example.com");
        Assert.StartsWith(Path.Combine(_root, "mail", "config"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SpamFilterState_rejects_traversal_but_keeps_valid_addresses()
    {
        var config = Config();

        MailSpamHelper.SetSpamFilterEnabled(config, "../../escape", enabled: false);
        Assert.False(File.Exists(Path.Combine(_root, "mail", "config", "escape.json")));

        Assert.True(MailSpamHelper.GetSpamFilterEnabled(config, "../../escape"));

        MailSpamHelper.SetSpamFilterEnabled(config, "user@example.com", enabled: false);
        Assert.False(MailSpamHelper.GetSpamFilterEnabled(config, "user@example.com"));
        Assert.True(File.Exists(Path.Combine(
            MailSpamHelper.SpamFilterDir(config), "user_example.com.json")));
    }
}
