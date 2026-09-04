using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;

namespace FeatherQuilld.Utils.Services;

/// <summary>
/// Polls FeatherPanel for runtime config and health, reacting to maintenance mode.
/// </summary>
public sealed class PanelSyncService : BackgroundService
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

    private readonly AppConfig _config;
    private readonly DaemonState _state;
    private readonly IPanelClient _panelClient;
    private readonly AppLogger? _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public PanelSyncService(AppConfig config, DaemonState state, IPanelClient panelClient, AppLogger? logger = null)
    {
        _config = config;
        _state = state;
        _panelClient = panelClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.HasPanelCredentials())
        {
            _logger?.Info(LoggerTypes.Application, "Panel sync disabled no remote credentials configured.");
            return;
        }

        _logger?.Info(LoggerTypes.Application, $"Panel sync started (interval {DefaultPollInterval.TotalSeconds}s).");

        try
        {
            await SyncOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(DefaultPollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SyncOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Soft shutdown do not log as panel sync failure.
        }
    }

    public async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken))
            return;

        try
        {
            await SyncHealthAsync(cancellationToken);
            await SyncConfigAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.PanelReachable = false;
            _state.LastPanelError = ex.Message;
            _logger?.Warning(LoggerTypes.Application, $"Panel sync failed: {ex.Message}");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncHealthAsync(CancellationToken cancellationToken)
    {
        var health = await _panelClient.FetchHealthAsync(cancellationToken);
        _state.PanelReachable = true;
        _state.LastPanelError = null;
        _state.MaintenanceMode = health.Data?.Node?.MaintenanceMode ?? false;

        if (_state.MaintenanceMode)
            _logger?.Warning(LoggerTypes.Application, "Panel reports maintenance mode daemon marked unhealthy.");
    }

    private async Task SyncConfigAsync(CancellationToken cancellationToken)
    {
        var runtimeYaml = await _panelClient.FetchRuntimeConfigYamlAsync(cancellationToken);
        _config.MergeRuntimeYaml(runtimeYaml);
        _config.EnsureDirectories();
        _config.Save();
        _logger?.Info(LoggerTypes.Application, "Runtime config synced from panel.");
    }
}
