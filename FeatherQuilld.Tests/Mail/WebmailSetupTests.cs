using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

public class WebmailSetupTests
{
    [Fact]
    public void BuildCompose_PinsImageAndTlsMailEndpoints()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig { ImapPort = 993, SmtpPort = 587 },
            },
        };

        var compose = WebmailSetup.BuildCompose(config);

        Assert.Contains(WebmailPaths.Image, compose);
        Assert.DoesNotContain(":latest", compose);
        Assert.Contains("ROUNDCUBEMAIL_DEFAULT_HOST=ssl://host.docker.internal", compose);
        Assert.Contains("ROUNDCUBEMAIL_DEFAULT_PORT=993", compose);
        Assert.Contains("ROUNDCUBEMAIL_SMTP_SERVER=tls://host.docker.internal", compose);
        Assert.Contains("ROUNDCUBEMAIL_SMTP_PORT=587", compose);
        Assert.Contains("127.0.0.1:8080:80", compose);
        Assert.Contains("./custom/token.php:/var/www/html/token.php:ro", compose);
        Assert.Contains("./sso.secret:/var/www/html/sso.secret:ro", compose);
    }

    [Fact]
    public void EnsureSsoSecret_CreatesAndReuses()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-webmail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig { RootDirectory = root },
            };

            var first = WebmailSetup.EnsureSsoSecret(config);
            var second = WebmailSetup.EnsureSsoSecret(config);
            Assert.Equal(first, second);
            Assert.True(first.Length >= 32);
            Assert.True(WebmailSetup.HasSsoSecret(config));
            Assert.True(File.Exists(Path.Combine(root, "webmail", "sso.secret")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EnsureCustomFiles_WritesTokenPhp()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-webmail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig { RootDirectory = root },
            };

            WebmailSetup.EnsureCustomFiles(config);
            var token = File.ReadAllText(Path.Combine(root, "webmail", "custom", "token.php"));
            Assert.Contains("hash_hmac", token);
            Assert.Contains("aes-256-gcm", token);
            Assert.DoesNotContain("$_GET['pass']", token);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData("webmail.example.com", true)]
    [InlineData("WEBMAIL.Example.COM.", true)]
    [InlineData("localhost", false)]
    [InlineData("http://bad", false)]
    [InlineData("", false)]
    public void IsValidHostname_ChecksFqdn(string hostname, bool expected)
    {
        Assert.Equal(expected, WebmailSetup.IsValidHostname(hostname));
    }
}
