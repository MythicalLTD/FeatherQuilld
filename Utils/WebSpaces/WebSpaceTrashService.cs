using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Soft-delete files into a hidden <c>.featherpanel-trash</c> folder (panel file manager parity).</summary>
public sealed class WebSpaceTrashService
{
    public const string TrashDirName = ".featherpanel-trash";
    private const string MetaFileName = "meta.json";
    private const string ContentDirName = "content";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IWebSpaceFsAccess _spaces;

    public WebSpaceTrashService(IWebSpaceFsAccess spaces) => _spaces = spaces;

    public static bool IsTrashPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var normalized = NormalizeRelative(relativePath);
        return normalized == TrashDirName || normalized.StartsWith(TrashDirName + "/", StringComparison.Ordinal);
    }

    public void MoveToTrash(Guid uuid, IEnumerable<string> relativePaths)
    {
        var root = RequireRoot(uuid);
        var trashRoot = EnsureTrashRoot(root);

        foreach (var rel in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(rel) || IsTrashPath(rel))
                continue;

            string sourcePath;
            try { sourcePath = WebSpaceFileService.ResolveExistingStatic(root, rel, mustBeDirectory: null); }
            catch { continue; }

            var fileName = Path.GetFileName(sourcePath);
            if (fileName is "webspace.json" or "site.json")
                continue;

            var normalized = NormalizeRelative(rel);
            var slash = normalized.LastIndexOf('/');
            var originalRoot = slash <= 0 ? "/" : "/" + normalized[..slash].TrimStart('/');
            var originalName = slash < 0 ? normalized : normalized[(slash + 1)..];

            var entryId = Guid.NewGuid().ToString("N");
            var entryDir = Path.Combine(trashRoot, entryId);
            var contentPath = Path.Combine(entryDir, ContentDirName);
            Directory.CreateDirectory(entryDir);

            var isDirectory = Directory.Exists(sourcePath);
            long size;
            if (isDirectory)
            {
                Directory.Move(sourcePath, contentPath);
                size = DirectorySize(contentPath);
            }
            else
            {
                Directory.CreateDirectory(entryDir);
                File.Move(sourcePath, contentPath);
                size = new FileInfo(contentPath).Length;
            }

            var meta = new TrashMeta
            {
                OriginalRoot = originalRoot,
                OriginalName = originalName,
                DeletedAt = DateTime.UtcNow.ToString("O"),
                IsDirectory = isDirectory,
                Size = size,
            };
            File.WriteAllText(Path.Combine(entryDir, MetaFileName), JsonSerializer.Serialize(meta, JsonOptions));
        }
    }

    public TrashListResult ListTrash(Guid uuid, long maxSizeBytes, int retentionDays)
    {
        var root = RequireRoot(uuid);
        var trashRoot = Path.Combine(root, TrashDirName);
        if (!Directory.Exists(trashRoot))
            return new TrashListResult([], 0);

        PurgeExpired(trashRoot, retentionDays);
        EnforceSizeLimit(trashRoot, maxSizeBytes);

        var entries = new List<TrashEntry>();
        long totalSize = 0;
        foreach (var entryDir in Directory.EnumerateDirectories(trashRoot))
        {
            var metaPath = Path.Combine(entryDir, MetaFileName);
            if (!File.Exists(metaPath))
                continue;

            TrashMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<TrashMeta>(File.ReadAllText(metaPath), JsonOptions);
            }
            catch
            {
                continue;
            }

            if (meta is null)
                continue;

            var id = Path.GetFileName(entryDir);
            var size = meta.Size > 0 ? meta.Size : EntrySize(entryDir);
            totalSize += size;
            entries.Add(new TrashEntry
            {
                Id = id,
                OriginalRoot = meta.OriginalRoot,
                OriginalName = meta.OriginalName,
                DeletedAt = meta.DeletedAt,
                Size = size,
                IsDirectory = meta.IsDirectory,
            });
        }

        entries.Sort((a, b) => string.Compare(b.DeletedAt, a.DeletedAt, StringComparison.Ordinal));
        return new TrashListResult(entries, totalSize);
    }

    public void RestoreTrash(Guid uuid, IEnumerable<string> ids, bool overwrite)
    {
        var root = RequireRoot(uuid);
        var trashRoot = Path.Combine(root, TrashDirName);

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var safeId = Path.GetFileName(id.Trim());
            var entryDir = Path.Combine(trashRoot, safeId);
            if (!Directory.Exists(entryDir))
                throw new FileNotFoundException($"Trash entry not found: {safeId}");

            var metaPath = Path.Combine(entryDir, MetaFileName);
            if (!File.Exists(metaPath))
                throw new InvalidOperationException($"Trash entry missing metadata: {safeId}");

            var meta = JsonSerializer.Deserialize<TrashMeta>(File.ReadAllText(metaPath), JsonOptions)
                ?? throw new InvalidOperationException($"Trash entry invalid metadata: {safeId}");

            var contentPath = Path.Combine(entryDir, ContentDirName);
            if (!File.Exists(contentPath) && !Directory.Exists(contentPath))
                throw new FileNotFoundException($"Trash entry content missing: {safeId}");

            var destDir = WebSpaceFileService.ResolveWritableStatic(root, meta.OriginalRoot);
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, meta.OriginalName);

            if (File.Exists(destPath) || Directory.Exists(destPath))
            {
                if (!overwrite)
                    throw new InvalidOperationException($"Restore target already exists: {meta.OriginalRoot}/{meta.OriginalName}");
                if (Directory.Exists(destPath))
                    Directory.Delete(destPath, recursive: true);
                else
                    File.Delete(destPath);
            }

            if (meta.IsDirectory)
                Directory.Move(contentPath, destPath);
            else
                File.Move(contentPath, destPath);

            Directory.Delete(entryDir, recursive: true);
        }
    }

    public void DeleteTrashEntries(Guid uuid, IEnumerable<string> ids)
    {
        var root = RequireRoot(uuid);
        var trashRoot = Path.Combine(root, TrashDirName);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var entryDir = Path.Combine(trashRoot, Path.GetFileName(id.Trim()));
            if (Directory.Exists(entryDir))
                Directory.Delete(entryDir, recursive: true);
        }
    }

    public void EmptyTrash(Guid uuid)
    {
        var root = RequireRoot(uuid);
        var trashRoot = Path.Combine(root, TrashDirName);
        if (!Directory.Exists(trashRoot))
            return;
        foreach (var entryDir in Directory.EnumerateDirectories(trashRoot))
        {
            try { Directory.Delete(entryDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private string RequireRoot(Guid uuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException("WebSpace not found.");
        return _spaces.EffectiveFsPath(uuid);
    }

    private static string EnsureTrashRoot(string root)
    {
        var trashRoot = Path.Combine(root, TrashDirName);
        Directory.CreateDirectory(trashRoot);
        return trashRoot;
    }

    private static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith('/'))
            normalized = normalized[1..];
        return normalized;
    }

    private static long DirectorySize(string path)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* ignore */ }
        }
        return total;
    }

    private static long EntrySize(string entryDir)
    {
        var contentPath = Path.Combine(entryDir, ContentDirName);
        if (File.Exists(contentPath))
            return new FileInfo(contentPath).Length;
        if (Directory.Exists(contentPath))
            return DirectorySize(contentPath);
        return 0;
    }

    private static void PurgeExpired(string trashRoot, int retentionDays)
    {
        if (retentionDays <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var entryDir in Directory.EnumerateDirectories(trashRoot))
        {
            var metaPath = Path.Combine(entryDir, MetaFileName);
            if (!File.Exists(metaPath))
                continue;
            try
            {
                var meta = JsonSerializer.Deserialize<TrashMeta>(File.ReadAllText(metaPath), JsonOptions);
                if (meta?.DeletedAt is null)
                    continue;
                if (DateTime.TryParse(meta.DeletedAt, out var deleted) && deleted < cutoff)
                    Directory.Delete(entryDir, recursive: true);
            }
            catch { /* ignore */ }
        }
    }

    private static void EnforceSizeLimit(string trashRoot, long maxSizeBytes)
    {
        if (maxSizeBytes <= 0)
            return;

        var entries = new List<(string Dir, TrashMeta Meta, long Size)>();
        long total = 0;
        foreach (var entryDir in Directory.EnumerateDirectories(trashRoot))
        {
            var metaPath = Path.Combine(entryDir, MetaFileName);
            if (!File.Exists(metaPath))
                continue;
            try
            {
                var meta = JsonSerializer.Deserialize<TrashMeta>(File.ReadAllText(metaPath), JsonOptions);
                if (meta is null)
                    continue;
                var size = meta.Size > 0 ? meta.Size : EntrySize(entryDir);
                total += size;
                entries.Add((entryDir, meta, size));
            }
            catch { /* ignore */ }
        }

        if (total <= maxSizeBytes)
            return;

        entries.Sort((a, b) => string.Compare(a.Meta.DeletedAt, b.Meta.DeletedAt, StringComparison.Ordinal));
        foreach (var (dir, _, _) in entries)
        {
            if (total <= maxSizeBytes)
                break;
            try
            {
                var size = EntrySize(dir);
                Directory.Delete(dir, recursive: true);
                total -= size;
            }
            catch { /* ignore */ }
        }
    }

    private sealed class TrashMeta
    {
        public string OriginalRoot { get; set; } = "/";
        public string OriginalName { get; set; } = "";
        public string DeletedAt { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
    }
}

public sealed class TrashEntry
{
    public string Id { get; init; } = "";
    public string OriginalRoot { get; init; } = "/";
    public string OriginalName { get; init; } = "";
    public string DeletedAt { get; init; } = "";
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
}

public sealed class TrashListResult
{
    public TrashListResult(IReadOnlyList<TrashEntry> entries, long totalSize)
    {
        Entries = entries;
        TotalSize = totalSize;
    }

    public IReadOnlyList<TrashEntry> Entries { get; }
    public long TotalSize { get; }
}
