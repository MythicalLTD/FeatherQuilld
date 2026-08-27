using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Plugins;
using FeatherQuilld.Utils.Startup;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Plugins;

public class PluginManagerTests
{
    [Fact]
    public void DiscoverAndLoad_DisabledSystem_IsSkipped()
    {
        using var logger = CreateLogger();
        var config = new AppConfig
        {
            Plugins = { Enabled = false, Directory = Path.GetTempPath() },
        };
        var manager = new PluginManager(config, logger);
        var result = manager.DiscoverAndLoad();
        Assert.Equal(BootStepStatus.Skipped, result.Status);
        Assert.Empty(manager.Plugins);
    }

    [Fact]
    public void DiscoverAndLoad_EmptyDirectory_SucceedsWithNoPlugins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fq-plugins-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var logger = CreateLogger();
            var config = new AppConfig { Plugins = { Enabled = true, Directory = dir } };
            var manager = new PluginManager(config, logger);
            var result = manager.DiscoverAndLoad();
            Assert.Equal(BootStepStatus.Success, result.Status);
            Assert.Empty(manager.Plugins);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAndLoad_HelloPluginFolder_LoadsPlugin()
    {
        var helloDll = typeof(FeatherQuilld.Plugins.Hello.HelloPlugin).Assembly.Location;
        Assert.True(File.Exists(helloDll), "Hello plugin assembly missing — build Hello first.");

        var dir = Path.Combine(Path.GetTempPath(), "fq-plugins-" + Guid.NewGuid());
        var pluginDir = Path.Combine(dir, "hello");
        Directory.CreateDirectory(pluginDir);
        File.Copy(helloDll, Path.Combine(pluginDir, Path.GetFileName(helloDll)));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.yml"), """
            id: hello
            name: Hello Plugin
            version: 0.1.0
            main: FeatherQuilld.Plugins.Hello.dll
            enabled: true
            """);

        // Sdk may be needed beside plugin depending on load context — copy if present next to Hello.
        var sdkBeside = Path.Combine(Path.GetDirectoryName(helloDll)!, "FeatherQuilld.Plugins.dll");
        if (File.Exists(sdkBeside))
            File.Copy(sdkBeside, Path.Combine(pluginDir, "FeatherQuilld.Plugins.dll"), overwrite: true);

        try
        {
            using var logger = CreateLogger();
            var config = new AppConfig { Plugins = { Enabled = true, Directory = dir } };
            var manager = new PluginManager(config, logger);
            var result = manager.DiscoverAndLoad();
            Assert.NotEqual(BootStepStatus.Failed, result.Status);
            Assert.Contains(manager.Plugins, p => p.Instance.Metadata.Id == "hello");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAndLoad_DisabledInConfig_SkipsHello()
    {
        var helloDll = typeof(FeatherQuilld.Plugins.Hello.HelloPlugin).Assembly.Location;
        var dir = Path.Combine(Path.GetTempPath(), "fq-plugins-dis-" + Guid.NewGuid());
        var pluginDir = Path.Combine(dir, "hello");
        Directory.CreateDirectory(pluginDir);
        File.Copy(helloDll, Path.Combine(pluginDir, Path.GetFileName(helloDll)));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.yml"), """
            id: hello
            name: Hello Plugin
            version: 0.1.0
            main: FeatherQuilld.Plugins.Hello.dll
            enabled: true
            """);
        try
        {
            using var logger = CreateLogger();
            var config = new AppConfig
            {
                Plugins = { Enabled = true, Directory = dir, Disabled = ["hello"] },
            };
            var manager = new PluginManager(config, logger);
            manager.DiscoverAndLoad();
            Assert.Empty(manager.Plugins);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverAndLoad_ManifestEnabledFalse_Skips()
    {
        var helloDll = typeof(FeatherQuilld.Plugins.Hello.HelloPlugin).Assembly.Location;
        var dir = Path.Combine(Path.GetTempPath(), "fq-plugins-off-" + Guid.NewGuid());
        var pluginDir = Path.Combine(dir, "hello");
        Directory.CreateDirectory(pluginDir);
        File.Copy(helloDll, Path.Combine(pluginDir, Path.GetFileName(helloDll)));
        File.WriteAllText(Path.Combine(pluginDir, "plugin.yml"), """
            id: hello
            enabled: false
            main: FeatherQuilld.Plugins.Hello.dll
            """);
        try
        {
            using var logger = CreateLogger();
            var config = new AppConfig { Plugins = { Enabled = true, Directory = dir } };
            var manager = new PluginManager(config, logger);
            manager.DiscoverAndLoad();
            Assert.Empty(manager.Plugins);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Logger CreateLogger()
    {
        var logs = Path.Combine(Path.GetTempPath(), "fq-test-logs-" + Guid.NewGuid());
        return new Logger(new LoggerOptions { Directory = logs, Debug = false, MaxArchives = 0 });
    }
}
