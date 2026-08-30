using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

public class MailDnsHelperTests
{
    [Fact]
    public void BuildSpf_IncludesMxAndHostname()
    {
        var spf = MailDnsHelper.BuildSpf("mail.example.com.");
        Assert.Contains("v=spf1", spf);
        Assert.Contains("mail.example.com", spf);
        Assert.Contains("-all", spf);
    }

    [Fact]
    public void ParseDkimTxt_ExtractsVdkim1Record()
    {
        const string raw = """
            mail._domainkey IN TXT ("v=DKIM1; k=rsa; p=abc123")
            """;

        var value = MailDnsHelper.ParseDkimTxt(raw);
        Assert.StartsWith("v=DKIM1", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p=abc123", value);
    }

    [Fact]
    public void BuildHints_IncludesMxAndSpfWithoutDkimFile()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Enabled = true,
                    Hostname = "mail.example.com",
                },
            },
        };

        var hints = MailDnsHelper.BuildHints(config, "example.com");
        Assert.Contains(hints, h => h.Type == "MX" && h.Priority == 10);
        Assert.Contains(hints, h => h.Type == "TXT" && h.Name == "@" && h.Value.StartsWith("v=spf1"));
        Assert.False(MailDnsHelper.IsDkimReady(config, "example.com"));
    }

    [Fact]
    public void ResolveMailHostname_PrefersConfiguredHostname()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig { Hostname = "mx.example.com" },
            },
        };

        Assert.Equal("mx.example.com.", MailDnsHelper.ResolveMailHostname(config, "other.test"));
    }

    [Fact]
    public void IsDkimReady_TrueWhenKeyFilePresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-mail-dkim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Mail = new MailConfig { Enabled = true, DkimSelector = "mail", DataPath = Path.Combine(root, "mail") },
                },
            };
            var keyDir = Path.Combine(MailPaths.MailStateDir(config), "opendkim", "keys", "example.com");
            Directory.CreateDirectory(keyDir);
            File.WriteAllText(Path.Combine(keyDir, "mail.txt"), "v=DKIM1; k=rsa; p=abc");

            Assert.True(MailDnsHelper.IsDkimReady(config, "example.com"));
            var payload = MailDnsHelper.BuildHintsPayload(config, "example.com");
            var dkimReady = (bool)payload.GetType().GetProperty("dkim_ready")!.GetValue(payload)!;
            Assert.True(dkimReady);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}

public class MailVacationHelperTests
{
    [Fact]
    public void BuildSieveScript_ContainsVacationAndEscapesQuotes()
    {
        var script = MailVacationHelper.BuildSieveScript(
            "user@example.com",
            "Out \"of\" office",
            "Line1\nLine2 with \"quotes\"");

        Assert.Contains("require [\"vacation\"]", script);
        Assert.Contains(":days 1", script);
        Assert.Contains("user@example.com", script);
        Assert.Contains("Out \\\"of\\\" office", script);
        Assert.Contains("Line1\\nLine2 with \\\"quotes\\\"", script);
        Assert.EndsWith(";", script.TrimEnd());
    }

    [Fact]
    public void WriteAndRemoveAutorespond_CreatesConfigSieve()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-mail-vac-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new AppConfig
            {
                System = new SystemConfig
                {
                    RootDirectory = root,
                    Mail = new MailConfig { DataPath = Path.Combine(root, "mail") },
                },
            };

            MailVacationHelper.WriteAutorespond(config, "alice@example.com", "Away", "Back soon");
            var path = MailVacationHelper.ConfigSievePath(config, "alice@example.com");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("vacation", content);
            Assert.Contains("Back soon", content);

            MailVacationHelper.RemoveAutorespond(config, "alice@example.com");
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}

public class HostPackageMailServerTests
{
    [Fact]
    public void List_IncludesMailserverPackage()
    {
        var mgr = new FeatherQuilld.Utils.SystemInfo.HostPackageManager();
        var packages = mgr.List();
        Assert.Contains(packages, p => p.Id == "mailserver");
    }
}
