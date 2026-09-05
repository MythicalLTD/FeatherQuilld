using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Path-confined filesystem ops under a WebSpace EffectiveFsPath (SFTP/CapFilesystem parity).</summary>
public sealed class WebSpaceFileService
{
    public const long DefaultPullMaxBytes = 100L * 1024L * 1024L;
    public static readonly TimeSpan DefaultPullTimeout = TimeSpan.FromMinutes(5);

    private readonly IWebSpaceFsAccess _spaces;
    private readonly HttpClient _http;
    private readonly IEventBus _events;

    public WebSpaceFileService(IWebSpaceFsAccess spaces, HttpClient? http = null, IEventBus? events = null)
    {
        _spaces = spaces;
        _http = http ?? CreateDefaultHttpClient();
        _events = events.OrNoOp();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Never follow redirects automatically: a URL that passes
            // ValidatePullUrl() could still redirect to an internal address
            // (e.g. http://169.254.169.254/...). Redirects are handled
            // manually in PullCoreAsync so each hop is re-validated.
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                // Resolve DNS ourselves and validate every resolved address
                // before connecting, instead of letting HttpClient connect
                // to whatever the OS resolver returns. ValidatePullUrl()
                // only rejects the URL if the *hostname itself* is a literal
                // blocked IP; a hostname that resolves to a private/loopback
                // address at request time (DNS rebinding) previously sailed
                // straight through.
                var host = context.DnsEndPoint.Host;
                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var literal))
                {
                    addresses = [literal];
                }
                else
                {
                    addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
                }

                if (addresses.Length == 0)
                    throw new InvalidOperationException($"Could not resolve host '{host}'.");

                foreach (var addr in addresses)
                {
                    if (IsBlockedIp(addr))
                        throw new InvalidOperationException($"Resolved address for '{host}' is not allowed.");
                }

                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        var client = new HttpClient(handler) { Timeout = DefaultPullTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FeatherQuilld/1.0");
        return client;
    }

    public IReadOnlyList<object> List(Guid uuid, string? directory) =>
        ListPaged(uuid, directory, page: 1, perPage: 0).Entries;

    public WebSpaceFileListResult ListPaged(Guid uuid, string? directory, int page = 1, int perPage = 250) =>
        _events.WithHooks(
            new FileListBeforeEvent { WebSpaceUuid = uuid, Directory = directory },
            (result, err) => new FileListAfterEvent
            {
                WebSpaceUuid = uuid,
                Directory = directory,
                Entries = result?.Entries,
                Error = err,
            },
            () => ListPagedCore(uuid, directory, page, perPage));

    private WebSpaceFileListResult ListPagedCore(Guid uuid, string? directory, int page, int perPage)
    {
        var all = ListCore(uuid, directory);
        var total = all.Count;
        if (perPage <= 0)
        {
            return new WebSpaceFileListResult(all, total, 1, total);
        }

        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 1000);
        var skip = (page - 1) * perPage;
        var slice = all.Skip(skip).Take(perPage).ToList();

        return new WebSpaceFileListResult(slice, total, page, perPage);
    }

    private IReadOnlyList<object> ListCore(Guid uuid, string? directory)
    {
        var root = RequireRoot(uuid);
        var dir = ResolveExisting(root, directory ?? "/", mustBeDirectory: true);
        var entries = new List<object>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(path);
            if (name is "webspace.json" or "site.json" or WebSpaceTrashService.TrashDirName)
                continue;
            var info = new FileInfo(path);
            var isDir = Directory.Exists(path);
            entries.Add(new
            {
                name,
                directory = isDir,
                file = !isDir,
                size = isDir ? 0L : info.Length,
                mime = isDir ? "inode/directory" : "application/octet-stream",
                modified_at = (isDir ? Directory.GetLastWriteTimeUtc(path) : info.LastWriteTimeUtc)
                    .ToString("O"),
                mode = FormatUnixMode(path, isDir),
            });
        }

        return entries.OrderByDescending(e => ((dynamic)e).directory).ThenBy(e => ((dynamic)e).name).ToList();
    }

    public string ReadText(Guid uuid, string file, long maxBytes = 5_000_000) =>
        _events.WithHooks(
            new FileReadBeforeEvent { WebSpaceUuid = uuid, Path = file },
            (contents, err) => new FileReadAfterEvent
            {
                WebSpaceUuid = uuid,
                Path = file,
                Contents = contents,
                Error = err,
            },
            () => ReadTextCore(uuid, file, maxBytes));

    private string ReadTextCore(Guid uuid, string file, long maxBytes)
    {
        var root = RequireRoot(uuid);
        var path = ResolveExisting(root, file, mustBeDirectory: false);
        var len = new FileInfo(path).Length;
        if (len > maxBytes)
            throw new InvalidOperationException($"File too large to edit ({len} bytes).");
        return File.ReadAllText(path, Encoding.UTF8);
    
    }

    public void WriteText(Guid uuid, string file, string contents) =>
        _events.WithHooks(
            new FileWriteBeforeEvent { WebSpaceUuid = uuid, Path = file },
            err => new FileWriteAfterEvent { WebSpaceUuid = uuid, Path = file, Error = err },
            () => WriteTextCore(uuid, file, contents));

        private void WriteTextCore(Guid uuid, string file, string contents)
    {
        var root = RequireRoot(uuid);
        var path = ResolveWritable(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents ?? "", Encoding.UTF8);
    
    }

    public void CreateDirectory(Guid uuid, string directory) =>
        _events.WithHooks(
            new FileCreateDirectoryBeforeEvent { WebSpaceUuid = uuid, Path = directory },
            err => new FileCreateDirectoryAfterEvent { WebSpaceUuid = uuid, Path = directory, Error = err },
            () => CreateDirectoryCore(uuid, directory));

        private void CreateDirectoryCore(Guid uuid, string directory)
    {
        var root = RequireRoot(uuid);
        var path = ResolveWritable(root, directory);
        Directory.CreateDirectory(path);
    
    }

    public void Rename(Guid uuid, string from, string to) =>
        _events.WithHooks(
            new FileRenameBeforeEvent { WebSpaceUuid = uuid, From = from, To = to },
            err => new FileRenameAfterEvent { WebSpaceUuid = uuid, From = from, To = to, Error = err },
            () => RenameCore(uuid, from, to));

        private void RenameCore(Guid uuid, string from, string to)
    {
        var root = RequireRoot(uuid);
        var src = ResolveExisting(root, from, mustBeDirectory: null);
        var dst = ResolveWritable(root, to);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (Directory.Exists(src))
            Directory.Move(src, dst);
        else
            File.Move(src, dst, overwrite: true);
    
    }

    /// <summary>
    /// Copy a file or directory within the WebSpace. When <paramref name="to"/> is null/empty,
    /// creates a sibling named like <c>name copy</c> / <c>name copy.ext</c>.
    /// Returns the destination virtual path.
    /// </summary>
    public string Copy(Guid uuid, string from, string? to = null) =>
        _events.WithHooks(
            new FileCopyBeforeEvent { WebSpaceUuid = uuid, From = from, To = to },
            (resultPath, err) => new FileCopyAfterEvent
            {
                WebSpaceUuid = uuid,
                From = from,
                To = to,
                ResultPath = resultPath,
                Error = err,
            },
            () => CopyCore(uuid, from, to));

    private string CopyCore(Guid uuid, string from, string? to)
    {
        var root = RequireRoot(uuid);
        var src = ResolveExisting(root, from, mustBeDirectory: null);
        var destVirtual = string.IsNullOrWhiteSpace(to)
            ? NextCopyVirtualPath(from)
            : to!;
        var dst = ResolveWritable(root, destVirtual);
        if (Path.GetFullPath(src) == Path.GetFullPath(dst))
            throw new InvalidOperationException("Source and destination are the same path.");

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (Directory.Exists(src))
            CopyDirectory(src, dst);
        else
            File.Copy(src, dst, overwrite: true);
        return NormalizeVirtual(destVirtual);
    
    }

    /// <summary>
    /// Copy each path into <paramref name="destination"/> (directory), or into each file's
    /// parent with an auto <c>name copy</c> sibling when destination is omitted.
    /// Returns entries with source <c>file</c> and destination <c>path</c>.
    /// </summary>
    public IReadOnlyList<object> CopyMany(Guid uuid, IReadOnlyList<string> files, string? destination = null)
    {
        if (files is null || files.Count == 0)
            throw new ArgumentException("files must be a non-empty list.");

        var root = RequireRoot(uuid);
        var destDirVirtual = string.IsNullOrWhiteSpace(destination)
            ? null
            : NormalizeVirtual(destination!);
        if (destDirVirtual is not null)
            _ = ResolveWritable(root, destDirVirtual);

        var results = new List<object>();
        foreach (var item in files)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            var from = NormalizeVirtual(item);
            var leaf = Path.GetFileName(from.TrimEnd('/'));
            if (leaf is "webspace.json" or "site.json" or "" or "." or "..")
                continue;

            var destVirtual = destDirVirtual is null
                ? NextCopyVirtualPath(from)
                : CombineVirtual(destDirVirtual, leaf);

            var path = Copy(uuid, from, destVirtual);
            results.Add(new { file = from, path });
        }

        if (results.Count == 0)
            throw new ArgumentException("No valid files to copy.");

        return results;
    }

    /// <summary>
    /// Create a symlink at <paramref name="link"/> pointing at <paramref name="target"/>
    /// (both confined under the WebSpace root).
    /// </summary>
    public void CreateSymlink(Guid uuid, string link, string target)
    {
        if (string.IsNullOrWhiteSpace(link))
            throw new ArgumentException("link is required.");
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("target is required.");

        var root = RequireRoot(uuid);
        var linkFull = ResolveWritable(root, link);
        var targetFull = ResolveExisting(root, target, mustBeDirectory: null);

        var linkLeaf = Path.GetFileName(linkFull);
        if (linkLeaf is "webspace.json" or "site.json")
            throw new InvalidOperationException("Cannot create symlink over protected file.");

        if (Path.Exists(linkFull))
            throw new InvalidOperationException("Destination already exists.");

        var linkDir = Path.GetDirectoryName(linkFull)
                      ?? throw new InvalidOperationException("Invalid link path.");
        EnsureUnderRoot(root, linkDir);
        Directory.CreateDirectory(linkDir);

        // Relative target keeps the link meaningful inside the jail.
        var relativeTarget = Path.GetRelativePath(linkDir, targetFull);
        File.CreateSymbolicLink(linkFull, relativeTarget);
    }

    /// <summary>
    /// Compute file hashes for the given virtual paths. Supported algorithms: sha1, sha256.
    /// Returns entries with <c>file</c> and <c>hash</c> (hex lowercase).
    /// </summary>
    public IReadOnlyList<object> Fingerprints(Guid uuid, IReadOnlyList<string> files, string algorithm = "sha256")
    {
        if (files is null || files.Count == 0)
            throw new ArgumentException("files must be a non-empty list.");

        var algo = (algorithm ?? "sha256").Trim().ToLowerInvariant();
        if (algo is not ("sha1" or "sha256"))
            throw new ArgumentException("algorithm must be sha1 or sha256.");

        var root = RequireRoot(uuid);
        var results = new List<object>();
        foreach (var item in files)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            string full;
            try { full = ResolveExisting(root, item, mustBeDirectory: false); }
            catch { continue; }

            var leaf = Path.GetFileName(full);
            if (leaf is "webspace.json" or "site.json")
                continue;

            var hash = HashFile(full, algo);
            results.Add(new { file = NormalizeVirtual(item), hash });
        }

        return results;
    }

    private static string HashFile(string path, string algorithm)
    {
        using var stream = File.OpenRead(path);
        byte[] digest = algorithm switch
        {
            "sha1" => SHA1.HashData(stream),
            _ => SHA256.HashData(stream),
        };
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public void Delete(Guid uuid, IEnumerable<string> files, bool useTrash = false, bool permanent = false)
    {
        if (useTrash && !permanent)
        {
            _events.WithHooks(
                new FileDeleteBeforeEvent { WebSpaceUuid = uuid, Paths = files.ToList() },
                err => new FileDeleteAfterEvent { WebSpaceUuid = uuid, Paths = files is IReadOnlyList<string> l ? l : files.ToList(), Error = err },
                () => new WebSpaceTrashService(_spaces).MoveToTrash(uuid, files));
            return;
        }

        _events.WithHooks(
            new FileDeleteBeforeEvent { WebSpaceUuid = uuid, Paths = files.ToList() },
            err => new FileDeleteAfterEvent { WebSpaceUuid = uuid, Paths = files is IReadOnlyList<string> l ? l : files.ToList(), Error = err },
            () => DeleteCore(uuid, files));
    }

        private void DeleteCore(Guid uuid, IEnumerable<string> files)
    {
        var root = RequireRoot(uuid);
        foreach (var rel in files)
        {
            if (string.IsNullOrWhiteSpace(rel))
                continue;
            string path;
            try { path = ResolveExisting(root, rel, mustBeDirectory: null); }
            catch { continue; }

            var name = Path.GetFileName(path);
            if (name is "webspace.json" or "site.json")
                continue;

            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
    
    }

    public Stream OpenRead(Guid uuid, string file)
    {
        var root = RequireRoot(uuid);
        var path = ResolveExisting(root, file, mustBeDirectory: false);
        return File.OpenRead(path);
    }

    public Task UploadAsync(Guid uuid, string directory, string fileName, Stream content, CancellationToken ct = default) =>
        _events.WithHooksAsync(
            new FileUploadBeforeEvent { WebSpaceUuid = uuid, Directory = directory, FileName = fileName },
            err => new FileUploadAfterEvent
            {
                WebSpaceUuid = uuid,
                Directory = directory,
                FileName = fileName,
                Error = err,
            },
            token => UploadCoreAsync(uuid, directory, fileName, content, token),
            ct);

    private async Task UploadCoreAsync(Guid uuid, string directory, string fileName, Stream content, CancellationToken ct)
    {
        var root = RequireRoot(uuid);
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("Invalid file name.");
        var dir = string.IsNullOrWhiteSpace(directory) || directory is "/" or "."
            ? root
            : ResolveWritable(root, directory);
        Directory.CreateDirectory(dir);
        var dest = ResolveWritable(root, CombineVirtual(directory, safeName));
        await using var fs = File.Create(dest);
        await content.CopyToAsync(fs, ct);
    
    }

    /// <summary>
    /// Compress <paramref name="files"/> into a zip or tar.gz under <paramref name="rootDirectory"/>.
    /// Returns the virtual path of the created archive.
    /// </summary>
    public string Compress(
        Guid uuid,
        string? rootDirectory,
        IReadOnlyList<string> files,
        string? archiveName = null,
        string extension = "tar.gz") =>
        _events.WithHooks(
            new FileCompressBeforeEvent { WebSpaceUuid = uuid, Paths = files },
            (archivePath, err) => new FileCompressAfterEvent
            {
                WebSpaceUuid = uuid,
                Paths = files,
                ArchivePath = archivePath,
                Error = err,
            },
            () => CompressCore(uuid, rootDirectory, files, archiveName, extension));

    private string CompressCore(
        Guid uuid,
        string? rootDirectory,
        IReadOnlyList<string> files,
        string? archiveName,
        string extension)
    {
        if (files is null || files.Count == 0)
            throw new ArgumentException("files must be a non-empty list.");

        var root = RequireRoot(uuid);
        var workDirVirtual = string.IsNullOrWhiteSpace(rootDirectory) ? "/" : rootDirectory!;
        var workDir = ResolveExisting(root, workDirVirtual, mustBeDirectory: true);
        _ = workDir;

        var ext = NormalizeArchiveExtension(extension);
        var baseName = string.IsNullOrWhiteSpace(archiveName)
            ? $"archive-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : Path.GetFileName(archiveName.Trim());
        if (!(baseName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
              || baseName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
              || baseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
              || baseName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
        {
            baseName += ext switch
            {
                "zip" => ".zip",
                "7z" => ".7z",
                _ => ".tar.gz",
            };
        }

        var archiveVirtual = CombineVirtual(workDirVirtual, baseName);
        var archivePath = ResolveWritable(root, archiveVirtual);
        if (File.Exists(archivePath) || Directory.Exists(archivePath))
            throw new InvalidOperationException($"Archive already exists: {baseName}");

        var sources = new List<(string FullPath, string EntryName)>();
        foreach (var item in files)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;
            var virtualPath = item.Contains('/') || item.StartsWith('/')
                ? item
                : CombineVirtual(workDirVirtual, item);
            var full = ResolveExisting(root, virtualPath, mustBeDirectory: null);
            var name = Path.GetFileName(full);
            if (name is "webspace.json" or "site.json")
                continue;
            sources.Add((full, name));
        }

        if (sources.Count == 0)
            throw new ArgumentException("No valid files to compress.");

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        if (ext == "zip")
            CreateZip(archivePath, sources);
        else if (ext == "7z")
            Create7z(archivePath, sources);
        else
            CreateTarGz(archivePath, sources);

        return RootedPath.ToVirtual(root, archivePath);
    
    }

    /// <summary>Extract a zip or tar.gz archive into its parent directory (zip-slip safe).</summary>
    public void Decompress(Guid uuid, string file) =>
        _events.WithHooks(
            new FileDecompressBeforeEvent { WebSpaceUuid = uuid, Path = file },
            err => new FileDecompressAfterEvent { WebSpaceUuid = uuid, Path = file, Error = err },
            () => DecompressCore(uuid, file));

        private void DecompressCore(Guid uuid, string file)
    {
        var root = RequireRoot(uuid);
        var archivePath = ResolveExisting(root, file, mustBeDirectory: false);
        var destDir = Path.GetDirectoryName(archivePath)
                      ?? throw new InvalidOperationException("Invalid archive path.");
        EnsureUnderRoot(root, destDir);

        var name = archivePath.ToLowerInvariant();
        if (name.EndsWith(".zip", StringComparison.Ordinal))
            ExtractZipSafe(archivePath, destDir, root);
        else if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
            ExtractTarGzSafe(archivePath, destDir, root);
        else if (name.EndsWith(".7z", StringComparison.Ordinal))
            Extract7zSafe(archivePath, destDir, root);
        else
            throw new InvalidOperationException("Unsupported archive type (use .zip, .tar.gz, or .7z).");
    
    }

    /// <summary>Apply Unix file modes (octal strings like 0644 / 755) to paths.</summary>
    public void Chmod(Guid uuid, IReadOnlyList<(string File, string Mode)> entries) =>
        _events.WithHooks(
            new FileChmodBeforeEvent { WebSpaceUuid = uuid, Entries = entries },
            err => new FileChmodAfterEvent { WebSpaceUuid = uuid, Error = err },
            () => ChmodCore(uuid, entries));

        private void ChmodCore(Guid uuid, IReadOnlyList<(string File, string Mode)> entries)
    {
        if (entries is null || entries.Count == 0)
            throw new ArgumentException("files must be a non-empty list.");

        var root = RequireRoot(uuid);
        foreach (var (file, modeStr) in entries)
        {
            if (string.IsNullOrWhiteSpace(file))
                continue;
            var path = ResolveExisting(root, file, mustBeDirectory: null);
            var leaf = Path.GetFileName(path);
            if (leaf is "webspace.json" or "site.json")
                continue;

            var mode = ParseOctalMode(modeStr);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(path, mode);
        }
    
    }

    /// <summary>
    /// Recursively search for file/directory names containing <paramref name="query"/> under
    /// <paramref name="directory"/>. Returns virtual paths (capped by <paramref name="limit"/>).
    /// </summary>
    public IReadOnlyList<object> Search(Guid uuid, string? directory, string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query is required.");

        limit = Math.Clamp(limit, 1, 500);
        var root = RequireRoot(uuid);
        var startVirtual = string.IsNullOrWhiteSpace(directory) ? "/" : directory!;
        var start = ResolveExisting(root, startVirtual, mustBeDirectory: true);
        var needle = query.Trim();
        var results = new List<object>();

        foreach (var path in Directory.EnumerateFileSystemEntries(start, "*", SearchOption.AllDirectories))
        {
            var virtualPath = RootedPath.ToVirtual(root, path);
            if (WebSpaceTrashService.IsTrashPath(virtualPath.TrimStart('/')))
                continue;

            var name = Path.GetFileName(path);
            if (name is "webspace.json" or "site.json" or WebSpaceTrashService.TrashDirName)
                continue;
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                var isDir = Directory.Exists(path);
                results.Add(new
                {
                    name,
                    directory = isDir,
                    file = !isDir,
                    path = RootedPath.ToVirtual(root, path),
                    size = isDir ? 0L : new FileInfo(path).Length,
                });
                if (results.Count >= limit)
                    break;
            }
        }

        return results;
    }

    public sealed class AdvancedSearchOptions
    {
        public string? Pattern { get; init; }
        public string? Include { get; init; }
        public string? Exclude { get; init; }
        public bool CaseInsensitive { get; init; } = true;
        public string? Content { get; init; }
        public bool ContentCaseInsensitive { get; init; } = true;
        public long? MinSize { get; init; }
        public long? MaxSize { get; init; }
        public long MaxContentSize { get; init; } = 512_000;
    }

    public IReadOnlyList<object> SearchAdvanced(
        Guid uuid,
        string? directory,
        AdvancedSearchOptions options,
        int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var root = RequireRoot(uuid);
        var startVirtual = string.IsNullOrWhiteSpace(directory) ? "/" : directory!;
        var start = ResolveExisting(root, startVirtual, mustBeDirectory: true);
        var results = new List<object>();
        var comparison = options.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var path in Directory.EnumerateFileSystemEntries(start, "*", SearchOption.AllDirectories))
        {
            if (WebSpaceTrashService.IsTrashPath(RootedPath.ToVirtual(root, path).TrimStart('/')))
                continue;

            var name = Path.GetFileName(path);
            if (name is "webspace.json" or "site.json" or WebSpaceTrashService.TrashDirName)
                continue;

            var isDir = Directory.Exists(path);
            var size = isDir ? 0L : new FileInfo(path).Length;
            if (options.MinSize is > 0 && size < options.MinSize)
                continue;
            if (options.MaxSize is > 0 && size > options.MaxSize)
                continue;

            var virtualPath = RootedPath.ToVirtual(root, path);
            if (!MatchesPattern(name, options.Pattern, comparison))
                continue;
            if (!MatchesPattern(name, options.Include, comparison, required: options.Include is not null))
                continue;
            if (options.Exclude is not null && MatchesPattern(name, options.Exclude, comparison, required: true))
                continue;

            if (!isDir && !string.IsNullOrWhiteSpace(options.Content))
            {
                if (size > options.MaxContentSize)
                    continue;
                try
                {
                    var text = File.ReadAllText(path);
                    var contentCmp = options.ContentCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (!text.Contains(options.Content, contentCmp))
                        continue;
                }
                catch
                {
                    continue;
                }
            }

            results.Add(new
            {
                name,
                directory = isDir,
                file = !isDir,
                path = virtualPath,
                size,
            });
            if (results.Count >= limit)
                break;
        }

        return results;
    }

    private static bool MatchesPattern(string name, string? pattern, StringComparison comparison, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return !required;
        if (pattern.Contains('*'))
        {
            var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
            var idx = 0;
            foreach (var part in parts)
            {
                var found = name.IndexOf(part, idx, comparison);
                if (found < 0)
                    return false;
                idx = found + part.Length;
            }
            return true;
        }
        return name.Contains(pattern, comparison);
    }

    /// <summary>Compress a directory to a temp tar.gz and return the file path (caller deletes).</summary>
    public string CreateDirectoryDownloadArchive(Guid uuid, string directory, string format = "tar.gz")
    {
        var root = RequireRoot(uuid);
        var dirVirtual = string.IsNullOrWhiteSpace(directory) ? "/" : directory;
        var dirPath = ResolveExisting(root, dirVirtual, mustBeDirectory: true);
        var dirName = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(dirName))
            dirName = "root";

        var ext = NormalizeArchiveExtension(format);
        var tempExt = ext switch { "zip" => "zip", "7z" => "7z", _ => "tar.gz" };
        var temp = Path.Combine(Path.GetTempPath(), $"fq-dl-{Guid.NewGuid():N}.{tempExt}");
        if (ext == "zip")
            CreateZip(temp, [(dirPath, dirName)]);
        else if (ext == "7z")
            Create7z(temp, [(dirPath, dirName)]);
        else
            CreateTarGz(temp, [(dirPath, dirName)]);
        return temp;
    }

    public ArchiveListResult ListArchiveDirectory(Guid uuid, string rootDirectory, string archiveFile, string? innerPath)
    {
        var root = RequireRoot(uuid);
        var archiveVirtual = archiveFile.Contains('/') || archiveFile.StartsWith('/')
            ? archiveFile
            : CombineVirtual(rootDirectory, archiveFile);
        var archivePath = ResolveExisting(root, archiveVirtual, mustBeDirectory: false);
        var prefix = NormalizeArchiveInnerPath(innerPath);

        var children = new Dictionary<string, (bool Dir, long Size, string Path)>(StringComparer.Ordinal);
        var lower = archivePath.ToLowerInvariant();
        if (lower.EndsWith(".zip"))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName))
                    continue;
                var rel = entry.FullName.Replace('\\', '/').TrimEnd('/');
                if (!TryArchiveChild(prefix, rel, entry.FullName.EndsWith('/'), entry.Length, out var child, out var childPath, out var isDir, out var size))
                    continue;
                MergeArchiveChild(children, child, childPath, isDir, size);
            }
        }
        else if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry() is { } entry)
            {
                var rel = (entry.Name ?? "").Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(rel))
                    continue;
                var isDir = entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList;
                if (!TryArchiveChild(prefix, rel, isDir, entry.Length, out var child, out var childPath, out var childIsDir, out var size))
                    continue;
                MergeArchiveChild(children, child, childPath, childIsDir, size);
            }
        }
        else if (lower.EndsWith(".7z"))
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Key))
                    continue;
                var rel = entry.Key.Replace('\\', '/').TrimEnd('/');
                if (!TryArchiveChild(prefix, rel, entry.IsDirectory, entry.Size, out var child, out var childPath, out var childIsDir, out var size))
                    continue;
                MergeArchiveChild(children, child, childPath, childIsDir, size);
            }
        }
        else
        {
            throw new InvalidOperationException("Unsupported archive format.");
        }

        var entries = children
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (object)new
            {
                name = kv.Key,
                path = kv.Value.Path,
                directory = kv.Value.Dir,
                size = kv.Value.Size,
            })
            .ToList();

        return new ArchiveListResult(entries.Take(500).ToList(), entries.Count > 500);
    }

    private static string NormalizeArchiveInnerPath(string? innerPath)
    {
        var prefix = (innerPath ?? "").Replace('\\', '/').Trim('/');
        if (prefix is "." or "/")
            prefix = "";
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";
        return prefix;
    }

    private static bool TryArchiveChild(
        string prefix,
        string rel,
        bool entryIsDirectory,
        long entryLength,
        out string child,
        out string childPath,
        out bool isDir,
        out long size)
    {
        child = "";
        childPath = "";
        isDir = false;
        size = 0;

        string rest;
        if (prefix.Length == 0)
            rest = rel;
        else if (rel.StartsWith(prefix, StringComparison.Ordinal))
            rest = rel[prefix.Length..];
        else if (rel == prefix.TrimEnd('/'))
            return false;
        else
            return false;

        if (rest.Length == 0)
            return false;

        var slash = rest.IndexOf('/');
        child = slash < 0 ? rest : rest[..slash];
        isDir = slash >= 0 || entryIsDirectory;
        childPath = prefix.Length == 0 ? child : prefix.TrimEnd('/') + "/" + child;
        size = slash < 0 && !isDir ? entryLength : 0;
        return true;
    }

    private static void MergeArchiveChild(
        Dictionary<string, (bool Dir, long Size, string Path)> children,
        string child,
        string childPath,
        bool isDir,
        long size)
    {
        if (children.TryGetValue(child, out var existing))
            children[child] = (existing.Dir || isDir, existing.Size + size, childPath);
        else
            children[child] = (isDir, size, childPath);
    }

    public void ExtractArchiveSelection(
        Guid uuid,
        string rootDirectory,
        string archiveFile,
        string destinationDirectory,
        IReadOnlyList<string> entries)
    {
        if (entries is null || entries.Count == 0)
            throw new ArgumentException("entries must be a non-empty list.");

        var root = RequireRoot(uuid);
        var archiveVirtual = archiveFile.Contains('/') || archiveFile.StartsWith('/')
            ? archiveFile
            : CombineVirtual(rootDirectory, archiveFile);
        var archivePath = ResolveExisting(root, archiveVirtual, mustBeDirectory: false);
        var destVirtual = string.IsNullOrWhiteSpace(destinationDirectory) ? "/" : destinationDirectory;
        var destDir = ResolveWritable(root, destVirtual);
        Directory.CreateDirectory(destDir);

        var wanted = new HashSet<string>(entries.Select(e => e.Replace('\\', '/').Trim('/')), StringComparer.Ordinal);
        var lower = archivePath.ToLowerInvariant();

        if (lower.EndsWith(".zip"))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                var rel = entry.FullName.Replace('\\', '/').TrimEnd('/');
                if (!wanted.Any(w => rel == w || rel.StartsWith(w + "/", StringComparison.Ordinal)))
                    continue;
                if (rel.StartsWith('/') || rel.Split('/').Any(p => p == ".."))
                    throw new UnauthorizedAccessException("Archive entry escapes WebSpace root.");
                var target = Path.GetFullPath(Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                EnsureUnderRoot(root, target);
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        else
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry() is { } entry)
            {
                var rel = (entry.Name ?? "").Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(rel))
                    continue;
                if (!wanted.Any(w => rel == w || rel.StartsWith(w + "/", StringComparison.Ordinal)))
                    continue;
                if (rel.StartsWith("..", StringComparison.Ordinal) || rel.Split('/').Any(p => p == ".."))
                    throw new UnauthorizedAccessException("Archive entry escapes WebSpace root.");
                var target = Path.GetFullPath(Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                EnsureUnderRoot(root, target);
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var outStream = File.Create(target);
                entry.DataStream?.CopyTo(outStream);
            }
        }
    }

    public void WipeAll(Guid uuid)
    {
        var root = RequireRoot(uuid);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            if (name is "webspace.json" or "site.json" or WebSpaceTrashService.TrashDirName)
                continue;
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
    }

    public sealed class ArchiveListResult
    {
        public ArchiveListResult(IReadOnlyList<object> contents, bool truncated)
        {
            Contents = contents;
            Truncated = truncated;
        }

        public IReadOnlyList<object> Contents { get; }
        public bool Truncated { get; }
    }

    /// <summary>
    /// Download a remote HTTP(S) URL into <paramref name="directory"/> under the WebSpace jail.
    /// Returns the virtual path of the saved file.
    /// </summary>
    public Task<string> PullAsync(
        Guid uuid,
        string? directory,
        string url,
        string? fileName = null,
        long maxBytes = DefaultPullMaxBytes,
        CancellationToken cancellationToken = default) =>
        _events.WithHooksAsync(
            new FilePullBeforeEvent { WebSpaceUuid = uuid, Url = url, Directory = directory },
            (resultPath, err) => new FilePullAfterEvent
            {
                WebSpaceUuid = uuid,
                Url = url,
                ResultPath = resultPath,
                Error = err,
            },
            token => PullCoreAsync(uuid, directory, url, fileName, maxBytes, token),
            cancellationToken);

    private async Task<string> PullCoreAsync(
        Guid uuid,
        string? directory,
        string url,
        string? fileName,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("url is required.");

        var uri = ValidatePullUrl(url);
        var root = RequireRoot(uuid);
        var dirVirtual = string.IsNullOrWhiteSpace(directory) ? "/" : directory!;
        var dir = string.IsNullOrWhiteSpace(directory) || directory is "/" or "."
            ? root
            : ResolveWritable(root, directory!);
        Directory.CreateDirectory(dir);

        var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName)
            ? GuessFileName(uri)
            : fileName!);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "download";

        var destVirtual = CombineVirtual(dirVirtual, safeName);
        var dest = ResolveWritable(root, destVirtual);

        using var response = await GetWithValidatedRedirectsAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Remote returned HTTP {(int)response.StatusCode}.");

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maxBytes)
            throw new InvalidOperationException($"Remote file exceeds max size ({maxBytes} bytes).");

        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fs = File.Create(dest);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await remote.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
                break;
            total += read;
            if (total > maxBytes)
            {
                fs.Close();
                try { File.Delete(dest); } catch { /* ignore */ }
                throw new InvalidOperationException($"Remote file exceeds max size ({maxBytes} bytes).");
            }

            await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return RootedPath.ToVirtual(root, dest);
    
    }

    /// <summary>
    /// GETs a URL, manually following up to 5 redirects and re-validating
    /// each target with <see cref="ValidatePullUrl"/> before following it.
    /// The HttpClient itself has AllowAutoRedirect disabled specifically so
    /// this re-validation can't be bypassed by a redirect response.
    /// </summary>
    private async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(Uri uri, CancellationToken cancellationToken)
    {
        const int maxRedirects = 5;
        var current = uri;
        for (var i = 0; ; i++)
        {
            var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
                throw new InvalidOperationException("Remote returned a redirect with no Location header.");
            if (i >= maxRedirects)
                throw new InvalidOperationException("Too many redirects.");

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            current = ValidatePullUrl(next.ToString());
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    /// <summary>Validate pull URL (http/https only; block obvious private/link-local targets).</summary>
    internal static Uri ValidatePullUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("url must be an absolute http(s) URL.");

        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("url must use http or https.");

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("url host is required.");

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("url host is not allowed.");

        if (IPAddress.TryParse(host, out var ip) && IsBlockedIp(ip))
            throw new ArgumentException("url host is not allowed.");

        return uri;
    }

    private static bool IsBlockedIp(IPAddress ip)
    {
        // Normalize IPv4-mapped IPv6 addresses (::ffff:127.0.0.1) to their
        // IPv4 form so the checks below apply uniformly instead of only
        // matching bare IPv4 addresses.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
            return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            if (bytes[0] == 10)
                return true;
            if (bytes[0] == 127)
                return true;
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            if (bytes[0] == 0)
                return true;
            // CGNAT range (RFC 6598), 100.64.0.0/10
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return true;
        }

        return false;
    }

    private static string GuessFileName(Uri uri)
    {
        var last = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        return Uri.UnescapeDataString(last);
    }

    private string RequireRoot(Guid uuid)
    {
        _ = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var root = RootedPath.CanonicalizeRoot(_spaces.EffectiveFsPath(uuid));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CombineVirtual(string? directory, string name)
    {
        var d = (directory ?? "/").Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(d) ? name : d + "/" + name;
    }

    private static string NormalizeVirtual(string virtualPath)
    {
        var p = (virtualPath ?? "/").Replace('\\', '/');
        if (!p.StartsWith('/'))
            p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);
        return p is "/" ? "/" : p.TrimEnd('/');
    }

    private static string NextCopyVirtualPath(string from)
    {
        var normalized = NormalizeVirtual(from);
        var fileName = Path.GetFileName(normalized);
        var dir = normalized.Contains('/')
            ? normalized[..normalized.LastIndexOf('/')]
            : "";
        if (string.IsNullOrEmpty(dir))
            dir = "/";

        string stem;
        string ext;
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            stem = fileName[..^7];
            ext = ".tar.gz";
        }
        else
        {
            ext = Path.GetExtension(fileName);
            stem = string.IsNullOrEmpty(ext) ? fileName : Path.GetFileNameWithoutExtension(fileName);
        }

        return CombineVirtual(dir, $"{stem} copy{ext}");
    }

    internal static string ResolveExistingStatic(string root, string virtualPath, bool? mustBeDirectory) =>
        ResolveExisting(root, virtualPath, mustBeDirectory);

    internal static string ResolveWritableStatic(string root, string virtualPath) =>
        ResolveWritable(root, virtualPath);

    private static string ResolveExisting(string root, string virtualPath, bool? mustBeDirectory)
    {
        var full = RootedPath.Resolve(root, virtualPath);
        if (mustBeDirectory == true)
        {
            if (!Directory.Exists(full))
                throw new FileNotFoundException("Directory not found.", virtualPath);
        }
        else if (mustBeDirectory == false)
        {
            if (!File.Exists(full))
                throw new FileNotFoundException("File not found.", virtualPath);
        }
        else if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException("Path not found.", virtualPath);
        }

        return full;
    }

    private static string ResolveWritable(string root, string virtualPath) =>
        RootedPath.Resolve(root, virtualPath, allowMissing: true);

    private static void EnsureUnderRoot(string root, string path)
    {
        if (!RootedPath.IsUnderRoot(root, Path.GetFullPath(path)))
            throw new UnauthorizedAccessException("Path escapes WebSpace root.");
    }

    private static string NormalizeArchiveExtension(string? extension)
    {
        var e = (extension ?? "tar.gz").Trim().TrimStart('.').ToLowerInvariant();
        return e switch
        {
            "zip" => "zip",
            "7z" => "7z",
            _ => "tar.gz",
        };
    }

    internal static UnixFileMode ParseOctalMode(string modeStr)
    {
        var s = (modeStr ?? "").Trim();
        if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (string.IsNullOrEmpty(s))
            throw new ArgumentException($"Invalid mode '{modeStr}'.");

        int parsed;
        try { parsed = Convert.ToInt32(s, 8); }
        catch { throw new ArgumentException($"Invalid mode '{modeStr}'."); }

        if (parsed is < 0 or > 0xFFF)
            throw new ArgumentException($"Invalid mode '{modeStr}'.");

        return (UnixFileMode)parsed;
    }

    private static string FormatUnixMode(string path, bool isDir)
    {
        try
        {
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                return isDir ? "drwxr-xr-x" : "-rw-r--r--";

            var mode = File.GetUnixFileMode(path);
            var bits = (int)mode;
            var sb = new StringBuilder(10);
            sb.Append(isDir ? 'd' : '-');
            sb.Append((bits & (int)UnixFileMode.UserRead) != 0 ? 'r' : '-');
            sb.Append((bits & (int)UnixFileMode.UserWrite) != 0 ? 'w' : '-');
            sb.Append((bits & (int)UnixFileMode.UserExecute) != 0 ? 'x' : '-');
            sb.Append((bits & (int)UnixFileMode.GroupRead) != 0 ? 'r' : '-');
            sb.Append((bits & (int)UnixFileMode.GroupWrite) != 0 ? 'w' : '-');
            sb.Append((bits & (int)UnixFileMode.GroupExecute) != 0 ? 'x' : '-');
            sb.Append((bits & (int)UnixFileMode.OtherRead) != 0 ? 'r' : '-');
            sb.Append((bits & (int)UnixFileMode.OtherWrite) != 0 ? 'w' : '-');
            sb.Append((bits & (int)UnixFileMode.OtherExecute) != 0 ? 'x' : '-');
            return sb.ToString();
        }
        catch
        {
            return isDir ? "drwxr-xr-x" : "-rw-r--r--";
        }
    }

    private static void CreateZip(string archivePath, List<(string FullPath, string EntryName)> sources)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (full, entryName) in sources)
        {
            if (Directory.Exists(full))
                AddDirectoryToZip(zip, full, entryName);
            else
                zip.CreateEntryFromFile(full, entryName, CompressionLevel.Optimal);
        }
    }

    private static void AddDirectoryToZip(ZipArchive zip, string dirPath, string entryPrefix)
    {
        var prefix = entryPrefix.TrimEnd('/') + "/";
        zip.CreateEntry(prefix);
        foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dirPath, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, prefix + rel, CompressionLevel.Optimal);
        }

        foreach (var sub in Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dirPath, sub).Replace('\\', '/');
            zip.CreateEntry(prefix + rel.TrimEnd('/') + "/");
        }
    }

    private static void CreateTarGz(string archivePath, List<(string FullPath, string EntryName)> sources)
    {
        var staging = Path.Combine(Path.GetTempPath(), "fq-compress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (full, entryName) in sources)
            {
                var dest = Path.Combine(staging, entryName);
                if (Directory.Exists(full))
                    CopyDirectory(full, dest);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(full, dest, overwrite: true);
                }
            }

            using var file = File.Create(archivePath);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            TarFile.CreateFromDirectory(staging, gzip, includeBaseDirectory: false);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ExtractZipSafe(string archivePath, string destDir, string jailRoot)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName))
                continue;
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.Split('/').Any(p => p == ".."))
                throw new UnauthorizedAccessException("Archive entry escapes WebSpace root.");

            var target = Path.GetFullPath(Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureUnderRoot(jailRoot, target);

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void Create7z(string archivePath, List<(string FullPath, string EntryName)> sources)
    {
        using var stream = File.Create(archivePath);
        using var writer = WriterFactory.OpenWriter(
            stream,
            ArchiveType.SevenZip,
            new SevenZipWriterOptions(CompressionType.LZMA2));
        foreach (var (full, entryName) in sources)
        {
            if (Directory.Exists(full))
                AddDirectoryTo7z(writer, full, entryName);
            else
            {
                using var fileStream = File.OpenRead(full);
                writer.Write(Path.GetFileName(entryName), fileStream, File.GetLastWriteTimeUtc(full));
            }
        }
    }

    private static void AddDirectoryTo7z(IWriter writer, string dirPath, string entryPrefix)
    {
        var prefix = entryPrefix.TrimEnd('/') + "/";
        foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dirPath, file).Replace('\\', '/');
            using var fs = File.OpenRead(file);
            writer.Write(prefix + rel, fs, File.GetLastWriteTimeUtc(file));
        }
    }

    private static void Extract7zSafe(string archivePath, string destDir, string jailRoot)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                continue;

            var relative = entry.Key.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.Split('/').Any(p => p == ".."))
                throw new UnauthorizedAccessException("Archive entry escapes WebSpace root.");

            var target = Path.GetFullPath(Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureUnderRoot(jailRoot, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var entryStream = entry.OpenEntryStream();
            using var outStream = File.Create(target);
            entryStream.CopyTo(outStream);
        }
    }

    private static void ExtractTarGzSafe(string archivePath, string destDir, string jailRoot)
    {
        var staging = Path.Combine(Path.GetTempPath(), "fq-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var file = File.OpenRead(archivePath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, path).Replace('\\', '/');
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
                    || rel.Split('/').Any(p => p == ".."))
                    throw new UnauthorizedAccessException("Archive entry escapes WebSpace root.");

                var target = Path.GetFullPath(Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                EnsureUnderRoot(jailRoot, target);

                if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target, overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
        }
    }
}

public sealed record WebSpaceFileListResult(
    IReadOnlyList<object> Entries,
    int Total,
    int Page,
    int PerPage);
