using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Periodically renews Let's Encrypt certificates for SSL-enabled WebSpaces before they expire.
/// </summary>
public sealed class AcmeRenewalHostedService(
    AppConfig config,
    WebSpaceStore spaces,
    NginxAcmeService? acme = null,
    AppLogger? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
            do
            {
                try
                {
                    await RenewDueCertsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.Debug(LoggerTypes.Proxy, $"ACME renewal sweep: {ex.Message}");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RenewDueCertsAsync(CancellationToken cancellationToken)
    {
        if (acme is null || !config.System.Proxy.Enabled)
            return;

        var provider = (config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant();
        if (provider is not ("nginx" or "caddy"))
            return;

        foreach (var space in spaces.List())
        {
            if (!space.Ssl || space.Domains.Count == 0)
                continue;
            if (string.Equals(space.SslMode, "custom", StringComparison.OrdinalIgnoreCase))
                continue;

            var needsRenew = space.Domains.Any(d =>
                !string.IsNullOrWhiteSpace(d) && !NginxAcmeService.IsCertFresh(d.Trim().ToLowerInvariant()));

            if (!needsRenew)
                continue;

            try
            {
                await spaces.RenewSslAsync(space.Uuid, cancellationToken).ConfigureAwait(false);
                logger?.Info(LoggerTypes.Proxy, $"ACME auto-renew completed for WebSpace {space.Uuid}");
            }
            catch (Exception ex)
            {
                logger?.Warning(LoggerTypes.Proxy, $"ACME auto-renew failed for {space.Uuid}: {ex.Message}");
            }
        }
    }
}
