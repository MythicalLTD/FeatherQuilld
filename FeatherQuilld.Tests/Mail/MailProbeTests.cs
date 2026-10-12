using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using Xunit;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

/// <summary>
/// The mail port checks have to run against the host that actually runs the mail stack -
/// probing loopback for a remote mailcow reports "not listening" while mail is up.
/// </summary>
public class MailProbeTests
{
    [Fact]
    public void ProbeHost_UsesTheMailcowUrlHost()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Backend = "mailcow",
                    Mailcow = new MailcowConfig { Url = "https://mail.allo.bet" },
                },
            },
        };

        Assert.Equal("mail.allo.bet", MailProbe.ProbeHost(config));
    }

    [Fact]
    public void ProbeHost_FallsBackToTheConfiguredMailHostname()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Backend = "docker-mailserver",
                    Hostname = "mail.example.com",
                },
            },
        };

        Assert.Equal("mail.example.com", MailProbe.ProbeHost(config));
    }

    [Fact]
    public void ProbeHost_UsesLoopbackForALocalStack()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig { Backend = "docker-mailserver" },
            },
        };

        Assert.Equal("127.0.0.1", MailProbe.ProbeHost(config));
    }

    [Fact]
    public void ProbeHost_WithoutConfigUsesLoopback() =>
        Assert.Equal("127.0.0.1", MailProbe.ProbeHost(null));
}
