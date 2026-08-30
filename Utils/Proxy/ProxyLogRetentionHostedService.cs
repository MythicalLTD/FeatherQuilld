using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>Persists daily proxy access-log summaries so analytics can span more than the live log file.</summary>
public sealed class ProxyLogRetentionHostedService(
    AppConfig config,
    WebSpaceStore spaces,
    AppLogger? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                try
                {
                    ProxyAccessLogs.RotateAll(config.System.RootDirectory, spaces.List());
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
