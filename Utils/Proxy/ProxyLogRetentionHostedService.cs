using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>Persists daily proxy access-log summaries so analytics can span more than the live log file, and notices OWASP rule updates.</summary>
public sealed class ProxyLogRetentionHostedService(
    AppConfig config,
    WebSpaceStore spaces,
    WebSpaceBandwidthMeter? bandwidthMeter = null,
    AppLogger? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Every few minutes: pick up an updated OWASP rule set. Every few hours: summaries, log rotation, bandwidth sync.
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            var lastRotation = DateTime.MinValue;
            do
            {
                try
                {
                    if (DateTime.UtcNow - lastRotation >= TimeSpan.FromHours(6))
                    {
                        lastRotation = DateTime.UtcNow;
                        ProxyAccessLogs.RotateAll(config.System.RootDirectory, spaces.List());
                        bandwidthMeter?.SyncAll();
                    }

                    spaces.RefreshProxyIfRulesChanged();
                }
                catch (Exception ex)
                {
                    logger?.Debug(LoggerTypes.Proxy, $"proxy log retention: {ex.Message}");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
