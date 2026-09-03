namespace FeatherQuilld.Tests.Remote;

public class ConfigureLetsEncryptTests
{
    [Fact]
    public void ParsePortListeners_ExtractsProcessNames()
    {
        const string ss = """
            LISTEN 0 511 *:80 *:* users:(("caddy",pid=123,fd=8))
            LISTEN 0 511 *:443 *:* users:(("nginx",pid=456,fd=7))
            """;

        var processes = FeatherQuilld.Commands.ConfigureLetsEncrypt.ParsePortListeners(ss);
        Assert.Equal(["caddy", "nginx"], processes);
    }

    [Fact]
    public void CertificatePaths_UseLetsEncryptLiveLayout()
    {
        Assert.Equal(
            "/etc/letsencrypt/live/node.example.com/fullchain.pem",
            FeatherQuilld.Commands.ConfigureLetsEncrypt.CertPathFor("node.example.com"));
        Assert.Equal(
            "/etc/letsencrypt/live/node.example.com/privkey.pem",
            FeatherQuilld.Commands.ConfigureLetsEncrypt.KeyPathFor("node.example.com"));
    }

    [Fact]
    public void SummarizeCertbotError_PrefersDnsDetail_OverCommunityLink()
    {
        const string output = """
            Certbot failed to authenticate some domains (authenticator: standalone). The Certificate Authority reported these problems:
              Domain: daddy.01032008.xyz
              Type:   dns
              Detail: DNS problem: NXDOMAIN looking up A for daddy.01032008.xyz - check that a DNS record exists for this domain

            Hint: The Certificate Authority failed to download the challenge files from the temporary standalone webserver started by Certbot on port 80.

            Ask for help or search for solutions at https://community.letsencrypt.org. See the logfile /var/log/letsencrypt/letsencrypt.log or re-run Certbot with -v for more details.
            """;

        var summary = FeatherQuilld.Commands.ConfigureLetsEncrypt.SummarizeCertbotError(output, 1);
        Assert.Contains("NXDOMAIN", summary);
        Assert.Contains("A/AAAA", summary);
        Assert.DoesNotContain("community.letsencrypt.org", summary);
    }
}
