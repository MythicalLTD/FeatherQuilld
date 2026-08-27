using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.WebSpaces;

public enum BackupJobPhase
{
    Pending,
    Running,
    Completed,
    Failed,
}

public sealed record BackupJobState(
    Guid JobId,
    Guid WebSpaceUuid,
    string Operation,
    BackupJobPhase Phase,
    DateTimeOffset UpdatedAt,
    Guid? BackupUuid = null,
    long? Bytes = null,
    string? Checksum = null,
    string? Message = null);

/// <summary>
/// Async backup/restore job tracking with disk persistence under
/// <c>{System.RootDirectory}/jobs/backups/</c> so status survives daemon restarts.
/// Running jobs found on startup are marked failed.
/// </summary>
public sealed class BackupJobProgressService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly ConcurrentDictionary<Guid, BackupJobState> _jobs = new();
    private readonly string _jobsDir;

    public BackupJobProgressService(AppConfig? config = null)
    {
        var root = config?.System.RootDirectory ?? "/var/lib/featherquilld";
        _jobsDir = Path.Combine(root, "jobs", "backups");
        Directory.CreateDirectory(_jobsDir);
        RecoverFromDisk();
    }

    /// <summary>Use an explicit jobs directory (tests / custom layouts).</summary>
    public BackupJobProgressService(string jobsDirectory)
    {
        _jobsDir = jobsDirectory;
        Directory.CreateDirectory(_jobsDir);
        RecoverFromDisk();
    }

    public BackupJobState? Get(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var state))
            return state;

        var fromDisk = ReadFile(jobId);
        if (fromDisk is null)
            return null;

        _jobs[jobId] = fromDisk;
        return fromDisk;
    }

    public BackupJobState Start(Guid webspaceUuid, string operation)
    {
        var jobId = Guid.NewGuid();
        var state = new BackupJobState(
            jobId,
            webspaceUuid,
            operation,
            BackupJobPhase.Running,
            DateTimeOffset.UtcNow);
        Persist(state);
        return state;
    }

    public void MarkCompleted(Guid jobId, Guid? backupUuid = null, long? bytes = null, string? checksum = null) =>
        Update(jobId, BackupJobPhase.Completed, backupUuid, bytes, checksum, null);

    public void MarkFailed(Guid jobId, string message) =>
        Update(jobId, BackupJobPhase.Failed, null, null, null, message);

    private void Update(
        Guid jobId,
        BackupJobPhase phase,
        Guid? backupUuid,
        long? bytes,
        string? checksum,
        string? message)
    {
        var existing = Get(jobId);
        if (existing is null)
            return;

        Persist(existing with
        {
            Phase = phase,
            UpdatedAt = DateTimeOffset.UtcNow,
            BackupUuid = backupUuid ?? existing.BackupUuid,
            Bytes = bytes ?? existing.Bytes,
            Checksum = checksum ?? existing.Checksum,
            Message = message ?? existing.Message,
        });
    }

    private void Persist(BackupJobState state)
    {
        _jobs[state.JobId] = state;
        var path = JobPath(state.JobId);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private void RecoverFromDisk()
    {
        foreach (var file in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var state = JsonSerializer.Deserialize<BackupJobState>(json, JsonOptions);
                if (state is null || state.JobId == Guid.Empty)
                    continue;

                if (state.Phase is BackupJobPhase.Running or BackupJobPhase.Pending)
                {
                    state = state with
                    {
                        Phase = BackupJobPhase.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Message = "daemon restarted",
                    };
                    var path = JobPath(state.JobId);
                    File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
                }

                _jobs[state.JobId] = state;
            }
            catch
            {
                // ignore corrupt job files
            }
        }
    }

    private BackupJobState? ReadFile(Guid jobId)
    {
        var path = JobPath(jobId);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BackupJobState>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string JobPath(Guid jobId) =>
        Path.Combine(_jobsDir, $"{jobId:D}.json");
}
