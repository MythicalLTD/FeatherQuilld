using FeatherQuilld.Utils.Config.Sentry;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Config;

public class SentryConfigTests
{
    [Fact]
    public void Defaults_EnabledWithGlitchTipDsn()
    {
        var sentry = new SentryConfig();

        Assert.True(sentry.Enabled);
        Assert.Equal(SentryConfig.DefaultDsn, sentry.Dsn);
        Assert.Equal(0.01, sentry.TracesSampleRate);
        Assert.True(sentry.IsActive);
    }

    [Fact]
    public void Deserialize_OmitsSentry_UsesDefaults()
    {
        var config = AppConfig.DeserializeYaml("""
            app_name: FeatherQuilld
            debug: false
            """);

        Assert.True(config.Sentry.Enabled);
        Assert.Equal(SentryConfig.DefaultDsn, config.Sentry.Dsn);
        Assert.Equal(0.01, config.Sentry.TracesSampleRate);
    }

    [Fact]
    public void Deserialize_CanDisable()
    {
        var config = AppConfig.DeserializeYaml("""
            sentry:
              enabled: false
            """);

        Assert.False(config.Sentry.Enabled);
        Assert.False(config.Sentry.IsActive);
    }

    [Fact]
    public void Deserialize_CustomDsnAndSampleRate()
    {
        var config = AppConfig.DeserializeYaml("""
            sentry:
              enabled: true
              dsn: https://example@error.example/9
              traces_sample_rate: 0.25
            """);

        Assert.True(config.Sentry.IsActive);
        Assert.Equal("https://example@error.example/9", config.Sentry.Dsn);
        Assert.Equal(0.25, config.Sentry.TracesSampleRate);
    }

    [Fact]
    public void Serialize_RoundTripsSentrySection()
    {
        var config = new AppConfig
        {
            Sentry =
            {
                Enabled = false,
                Dsn = "https://x@host/1",
                TracesSampleRate = 0.5,
            },
        };

        var yaml = AppConfig.SerializeYaml(config);
        var loaded = AppConfig.DeserializeYaml(yaml);

        Assert.False(loaded.Sentry.Enabled);
        Assert.Equal("https://x@host/1", loaded.Sentry.Dsn);
        Assert.Equal(0.5, loaded.Sentry.TracesSampleRate);
    }
}
