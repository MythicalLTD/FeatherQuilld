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
