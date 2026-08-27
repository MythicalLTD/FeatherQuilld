using FeatherQuilld.Utils.Remote;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Fire-and-forget activity batches to the panel quilld-remote ingest route.</summary>
public sealed class WebSpaceActivityReporter(IPanelClient panel)
{
    public void Report(Guid webspaceUuid, string eventName, object? metadata = null)
    {
        _ = ReportAsync(webspaceUuid, eventName, metadata);
    }

    public async Task ReportAsync(
        Guid webspaceUuid,
        string eventName,
        object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await panel.ReportActivitiesAsync(
                [new PanelActivityEntry(webspaceUuid, eventName, metadata, Timestamp: DateTimeOffset.UtcNow)],
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Activity logging must not break hosting operations.
        }
    }
}
