using FeatherQuilld.Utils.Config;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Config;

public class ConfigMoreYamlTests
{
    [Fact]
    public void Deserialize_SftpAndPluginsSections()
    {
        const string yaml = """
            sftp:
              enabled: true
              port: 2222
              key_algorithm: ssh-ed25519
            plugins:
              enabled: true
              directory: /tmp/plugins
              strict: true
              disabled:
                - hello
                - other
            """;

        var config = AppConfig.DeserializeYaml(yaml);
        Assert.True(config.Sftp.Enabled);
        Assert.Equal(2222, config.Sftp.Port);
        Assert.Equal("ssh-ed25519", config.Sftp.KeyAlgorithm);
        Assert.True(config.Plugins.Enabled);
        Assert.True(config.Plugins.Strict);
        Assert.Equal("/tmp/plugins", config.Plugins.Directory);
        Assert.Equal(new[] { "hello", "other" }, config.Plugins.Disabled);
    }

    [Fact]
    public void ApplyDefaultPaths_SetsCanonicalLayout()
    {
        var config = new AppConfig();
        config.ApplyDefaultPaths();
        Assert.EndsWith("volumes", config.System.Data);
        Assert.EndsWith("backups", config.System.BackupDirectory);
        Assert.EndsWith("plugins", config.Plugins.Directory);
    }

    [Fact]
    public void ApplyLocalDevPaths_StaysUnderBaseDirectory()
    {
        var config = new AppConfig { FilePath = "/tmp/fq-dev/config.yml" };
        config.ApplyLocalDevPaths("/tmp/fq-dev");

        Assert.Equal("/tmp/fq-dev/data", config.System.RootDirectory);
        Assert.Equal("/tmp/fq-dev/logs", config.System.LogDirectory);
        Assert.Equal("/tmp/fq-dev/data/volumes", config.System.Data);
        Assert.Equal("/tmp/fq-dev/data/plugins", config.Plugins.Directory);
        Assert.DoesNotContain("/var/log", config.System.LogDirectory);
    }

    [Fact]
    public void Load_MissingDefaultPath_ThrowsConfigNotReady()
    {
        if (File.Exists(AppConfig.DefaultPath()))
            return;

        var ex = Assert.Throws<ConfigNotReadyException>(() => AppConfig.Load());
        Assert.Contains("sudo quilld configure", ex.Message);
    }

    [Fact]
    public void IsJoinedToPanel_RejectsPlaceholderAndEmptyPanel()
    {
        var stub = new AppConfig
        {
            TokenId = "fqld_abc",
            Token = "secret",
            Remote = { Panel = AppConfig.PlaceholderPanelUrl },
        };
        Assert.False(stub.IsJoinedToPanel());

        stub.Remote.Panel = "";
        Assert.False(stub.IsJoinedToPanel());

        stub.Remote.Panel = "https://panel.example.com";
        Assert.True(stub.IsJoinedToPanel());
    }

    [Fact]
    public void Load_ExplicitMissingPath_UsesLocalLayout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.yml");

        try
        {
            var config = AppConfig.Load(path);
            Assert.True(File.Exists(path));
            Assert.StartsWith(dir, config.System.LogDirectory);
            Assert.StartsWith(dir, config.System.RootDirectory);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void HasPanelCredentials_RequiresPanelAndTokens()
    {
        var config = new AppConfig
        {
            TokenId = "id",
            Token = "secret",
            Remote = { Panel = "http://panel" },
        };
        Assert.True(config.HasPanelCredentials());
        config.Remote.Panel = "";
        Assert.False(config.HasPanelCredentials());
    }
}
