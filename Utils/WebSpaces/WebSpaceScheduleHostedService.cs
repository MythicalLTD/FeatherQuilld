namespace FeatherQuilld.Utils.WebSpaces;

public sealed class WebSpaceScheduleHostedService(WebSpaceScheduleManager scheduleManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await scheduleManager.SyncAllFromPanelAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await scheduleManager.RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Soft shutdown BackgroundService treats this as a clean stop.
        }
    }
}
