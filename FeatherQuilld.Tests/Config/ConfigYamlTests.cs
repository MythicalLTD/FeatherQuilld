using FeatherQuilld.Utils.Config;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Config;

public class ConfigYamlTests
{
    [Fact]
    public void DeserializeYaml_MapsBackupDirectoryAlias()
    {
        const string yaml = """
            system:
              data: /tmp/fq-test/volumes
              backup_directory: /tmp/fq-test/backups-custom
            """;

        var config = AppConfig.DeserializeYaml(yaml);

        Assert.Equal("/tmp/fq-test/backups-custom", config.System.BackupDirectory);
        Assert.Equal("/tmp/fq-test/volumes", config.System.Data);
    }

    [Fact]
    public void SerializeYaml_RoundTripsRemotePanel()
    {
        var config = new AppConfig
        {
            Uuid = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TokenId = "tid",
            Token = "tok",
            Remote = { Panel = "http://panel.local:8721" },
        };

        var yaml = AppConfig.SerializeYaml(config);
        var loaded = AppConfig.DeserializeYaml(yaml);

        Assert.Equal(config.Uuid, loaded.Uuid);
        Assert.Equal("tid", loaded.TokenId);
        Assert.Equal("tok", loaded.Token);
        Assert.Equal("http://panel.local:8721", loaded.Remote.Panel);
    }
}
