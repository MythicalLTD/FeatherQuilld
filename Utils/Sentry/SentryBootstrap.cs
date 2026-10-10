using FeatherQuilld.Utils.Config.Sentry;
using FeatherQuilld.Utils.Startup;
using Sentry;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Sentry;

/// <summary>Initializes and flushes the GlitchTip / Sentry SDK from node config.</summary>
public static class SentryBootstrap
{
    private static bool _initialized;

    public static bool IsInitialized => _initialized;

    public static void Init(AppConfig config)
    {
        if (_initialized || !config.Sentry.IsActive)
            return;

        var sentry = config.Sentry;
        SentrySdk.Init(options =>
        {
            options.Dsn = sentry.Dsn.Trim();
            options.TracesSampleRate = Math.Clamp(sentry.TracesSampleRate, 0, 1);
            options.Release = $"featherquilld@{StartupBanner.Version}";
            options.Environment = config.Debug ? "development" : "production";
            options.AutoSessionTracking = true;
            options.SendDefaultPii = false;
            options.DefaultTags["app"] = config.AppName;
            options.DefaultTags["node_uuid"] = config.Uuid.ToString("D");
        });

        _initialized = true;
    }

    public static void Flush(TimeSpan? timeout = null)
    {
        if (!_initialized)
            return;

        try
        {
            SentrySdk.FlushAsync(timeout ?? TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // best-effort on shutdown
        }
    }
}
