using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.WebSpaces;

public enum TransferPhase
{
    Idle,
    Running,
    Completed,
    Failed,
}

public sealed record TransferProgressState(
    Guid Uuid,
    TransferPhase Phase,
    string Direction,
    DateTimeOffset UpdatedAt,
    string? Message = null);

/// <summary>
/// Transfer progress with disk persistence under
/// <c>{System.RootDirectory}/jobs/transfers/</c> so status survives daemon restarts.
/// Running transfers found on startup are marked failed.
/// </summary>
public sealed class TransferProgressService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly ConcurrentDictionary<Guid, TransferProgressState> _states = new();
    private readonly string _jobsDir;

    public TransferProgressService(AppConfig? config = null)
    {
        var root = config?.System.RootDirectory ?? "/var/lib/featherquilld";
        _jobsDir = Path.Combine(root, "jobs", "transfers");
        Directory.CreateDirectory(_jobsDir);
        RecoverFromDisk();
    }

    /// <summary>Use an explicit jobs directory (tests / custom layouts).</summary>
    public TransferProgressService(string jobsDirectory)
    {
        _jobsDir = jobsDirectory;
        Directory.CreateDirectory(_jobsDir);
        RecoverFromDisk();
    }

    public TransferProgressState? Get(Guid uuid)
    {
        if (_states.TryGetValue(uuid, out var state))
            return state;

        var fromDisk = ReadFile(uuid);
        if (fromDisk is null)
            return null;

        _states[uuid] = fromDisk;
        return fromDisk;
    }

    public void MarkRunning(Guid uuid, string direction, string? message = null) =>
        Persist(new TransferProgressState(uuid, TransferPhase.Running, direction, DateTimeOffset.UtcNow, message));

    public void MarkCompleted(Guid uuid, string direction) =>
        Persist(new TransferProgressState(uuid, TransferPhase.Completed, direction, DateTimeOffset.UtcNow));

    public void MarkFailed(Guid uuid, string direction, string message) =>
        Persist(new TransferProgressState(uuid, TransferPhase.Failed, direction, DateTimeOffset.UtcNow, message));

    private void Persist(TransferProgressState state)
    {
        _states[state.Uuid] = state;
        var path = JobPath(state.Uuid);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private void RecoverFromDisk()
    {
        foreach (var file in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<TransferProgressState>(File.ReadAllText(file), JsonOptions);
                if (state is null || state.Uuid == Guid.Empty)
                    continue;

                if (state.Phase is TransferPhase.Running or TransferPhase.Idle)
                {
                    state = state with
                    {
                        Phase = TransferPhase.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Message = "daemon restarted",
                    };
                    File.WriteAllText(JobPath(state.Uuid), JsonSerializer.Serialize(state, JsonOptions));
                }

                _states[state.Uuid] = state;
            }
            catch
            {
                // ignore corrupt files
            }
        }
    }

    private TransferProgressState? ReadFile(Guid uuid)
    {
        var path = JobPath(uuid);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<TransferProgressState>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string JobPath(Guid uuid) =>
        Path.Combine(_jobsDir, $"{uuid:D}.json");
}
