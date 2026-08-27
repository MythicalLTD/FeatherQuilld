using Cronos;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.WebSpaces.Schedules;

namespace FeatherQuilld.Utils.WebSpaces;

public sealed class WebSpaceScheduleManager(
    WebSpaceStore spaces,
    WebSpaceBackupService backupService,
    IPanelClient panel,
    WebSpaceActivityReporter? activityReporter,
    ILogger<WebSpaceScheduleManager> logger)
{
    private readonly Dictionary<string, List<ScheduledEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void SyncSchedules(string webSpaceUuid, IReadOnlyList<WebSpaceScheduleDefinition> schedules)
    {
        lock (_lock)
        {
            _entries.Remove(webSpaceUuid);
            if (schedules.Count == 0)
            {
                return;
            }

            var list = new List<ScheduledEntry>();
            foreach (var schedule in schedules.Where(s => s.IsActive))
            {
                try
                {
                    var expr = CronExpression.Parse(
                        $"{schedule.CronMinute} {schedule.CronHour} {schedule.CronDayOfMonth} {schedule.CronMonth} {schedule.CronDayOfWeek}",
                        CronFormat.Standard);
                    var tz = ResolveTimeZone(schedule.Timezone);
                    list.Add(new ScheduledEntry(schedule, expr, tz));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping invalid schedule {ScheduleId} for webspace {Uuid}", schedule.Id, webSpaceUuid);
                }
            }

            if (list.Count > 0)
            {
                _entries[webSpaceUuid] = list;
            }
        }
    }

    public async Task SyncWebSpaceFromPanelAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await panel.FetchWebSpaceAsync(uuid, cancellationToken).ConfigureAwait(false);
            SyncSchedules(uuid.ToString("D"), MapSchedules(config.Schedules));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Soft shutdown / host stop — do not treat as a sync failure.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync schedules for webspace {Uuid}", uuid);
        }
    }

    public async Task SyncAllFromPanelAsync(CancellationToken cancellationToken = default)
    {
        foreach (var space in spaces.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncWebSpaceFromPanelAsync(space.Uuid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    public void RemoveSchedules(string webSpaceUuid)
    {
        lock (_lock)
        {
            _entries.Remove(webSpaceUuid);
        }
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        List<(string Uuid, WebSpaceScheduleDefinition Schedule)> due = [];

        lock (_lock)
        {
            foreach (var (uuid, entries) in _entries)
            {
                foreach (var entry in entries)
                {
                    if (entry.LastRunUtc.HasValue && now - entry.LastRunUtc.Value < TimeSpan.FromMinutes(1))
                    {
                        continue;
                    }

                    var next = entry.Expression.GetNextOccurrence(now.UtcDateTime, entry.TimeZone);
                    if (next.HasValue && next.Value <= now.UtcDateTime.AddSeconds(30))
                    {
                        due.Add((uuid, entry.Definition));
                        entry.LastRunUtc = now;
                    }
                }
            }
        }

        foreach (var (uuid, schedule) in due)
        {
            await ExecuteScheduleAsync(uuid, schedule, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> TriggerAsync(string webSpaceUuid, int scheduleId, CancellationToken cancellationToken)
    {
        WebSpaceScheduleDefinition? schedule;
        lock (_lock)
        {
            if (!_entries.TryGetValue(webSpaceUuid, out var entries))
            {
                return false;
            }

            schedule = entries.Select(e => e.Definition).FirstOrDefault(s => s.Id == scheduleId);
        }

        if (schedule is null)
        {
            return false;
        }

        await ExecuteScheduleAsync(webSpaceUuid, schedule, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public bool Abort(string webSpaceUuid)
    {
        lock (_lock)
        {
            return _running.Remove(webSpaceUuid);
        }
    }

    public bool IsRunning(string webSpaceUuid)
    {
        lock (_lock)
        {
            return _running.Contains(webSpaceUuid);
        }
    }

    private async Task ExecuteScheduleAsync(string webSpaceUuid, WebSpaceScheduleDefinition schedule, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_running.Add(webSpaceUuid))
            {
                logger.LogInformation(
                    "Schedule {ScheduleId} skipped; webspace {Uuid} already running a schedule",
                    schedule.Id,
                    webSpaceUuid);
                return;
            }
        }

        try
        {
            logger.LogInformation(
                "Running schedule {ScheduleId} ({Name}) for webspace {Uuid}",
                schedule.Id,
                schedule.Name,
                webSpaceUuid);

            if (Guid.TryParse(webSpaceUuid, out var wsUuid))
            {
                activityReporter?.Report(wsUuid, "schedule_executed", new { schedule_id = schedule.Id, schedule_name = schedule.Name });
            }

            foreach (var task in schedule.Tasks.OrderBy(t => t.SequenceId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (task.TimeOffset > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(task.TimeOffset), cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await ExecuteTaskAsync(webSpaceUuid, task, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Schedule task {TaskId} failed for webspace {Uuid}", task.Id, webSpaceUuid);
                    if (!task.ContinueOnFailure)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(webSpaceUuid);
            }
        }
    }

    private Task ExecuteTaskAsync(string webSpaceUuid, WebSpaceScheduleTaskDefinition task, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(webSpaceUuid, out var uuid))
        {
            throw new InvalidOperationException($"Invalid webspace uuid '{webSpaceUuid}'.");
        }

        _ = cancellationToken;
        switch (task.Action.Trim().ToLowerInvariant())
        {
            case "power":
            case "restart":
                spaces.Power(uuid, "restart");
                break;
            case "stop":
                spaces.Power(uuid, "stop");
                break;
            case "start":
                spaces.Power(uuid, "start");
                break;
            case "backup":
                backupService.Create(uuid, stopDuringBackup: true);
                break;
            default:
                logger.LogWarning("Unknown schedule action {Action} for webspace {Uuid}", task.Action, webSpaceUuid);
                break;
        }

        return Task.CompletedTask;
    }

    private static TimeZoneInfo ResolveTimeZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim());
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    internal static List<WebSpaceScheduleDefinition> MapSchedules(IReadOnlyList<PanelWebSpaceSchedule>? schedules)
    {
        if (schedules is null || schedules.Count == 0)
        {
            return [];
        }

        return schedules.Select(s => new WebSpaceScheduleDefinition
        {
            Id = s.Id,
            Name = s.Name,
            CronMinute = s.CronMinute,
            CronHour = s.CronHour,
            CronDayOfMonth = s.CronDayOfMonth,
            CronMonth = s.CronMonth,
            CronDayOfWeek = s.CronDayOfWeek,
            Timezone = s.Timezone,
            IsActive = s.IsActive,
            Tasks = s.Tasks.Select(t => new WebSpaceScheduleTaskDefinition
            {
                Id = t.Id,
                SequenceId = t.SequenceId,
                Action = t.Action,
                Payload = t.Payload,
                TimeOffset = t.TimeOffset,
                ContinueOnFailure = t.ContinueOnFailure,
            }).ToList(),
        }).ToList();
    }

    private sealed class ScheduledEntry(WebSpaceScheduleDefinition definition, CronExpression expression, TimeZoneInfo timeZone)
    {
        public WebSpaceScheduleDefinition Definition { get; } = definition;
        public CronExpression Expression { get; } = expression;
        public TimeZoneInfo TimeZone { get; } = timeZone;
        public DateTimeOffset? LastRunUtc { get; set; }
    }
}
