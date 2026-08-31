using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Outgoing archive push + incoming extract for cross-node WebSpace transfers.</summary>
public sealed class WebSpaceTransferService
{
    private readonly AppConfig _config;
    private readonly WebSpaceStore _spaces;
    private readonly IPanelClient _panel;
    private readonly TransferProgressService _progress;
    private readonly HttpClient _http;
    private readonly AppLogger? _logger;
    private readonly IEventBus _events;

    public WebSpaceTransferService(
        AppConfig config,
        WebSpaceStore spaces,
        IPanelClient panel,
        TransferProgressService? progress = null,
        HttpClient? http = null,
        AppLogger? logger = null,
        IEventBus? events = null)
    {
        _config = config;
        _spaces = spaces;
        _panel = panel;
        _progress = progress ?? new TransferProgressService(config);
        _http = http ?? CreateHttpClient();
        _logger = logger;
        _events = events.OrNoOp();
    }

    public TransferProgressState? GetProgress(Guid uuid) => _progress.Get(uuid);

    private static HttpClient CreateHttpClient() =>
        new() { Timeout = TimeSpan.FromHours(2) };

    /// <summary>
    /// Stop runtime, stream tar.gz to destination <paramref name="uploadUrl"/>, delete local on success.
    /// </summary>
    public Task OutgoingAsync(
        Guid uuid,
        string uploadUrl,
        string bearerToken,
        bool startOnCompletion = true,
        bool includeBackups = false,
        CancellationToken cancellationToken = default) =>
        _events.WithHooksAsync(
            new TransferOutgoingBeforeEvent { WebSpaceUuid = uuid, UploadUrl = uploadUrl, StartOnCompletion = startOnCompletion },
            err => new TransferOutgoingAfterEvent { WebSpaceUuid = uuid, Error = err },
            token => OutgoingCoreAsync(uuid, uploadUrl, bearerToken, startOnCompletion, includeBackups, token),
            cancellationToken);

    private async Task OutgoingCoreAsync(
        Guid uuid,
        string uploadUrl,
        string bearerToken,
        bool startOnCompletion,
        bool includeBackups,
        CancellationToken cancellationToken)
    {
        _progress.MarkRunning(uuid, "outgoing");
        var space = _spaces.Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var wasRunning = space.State == WebSpaceState.Running && WebSpaceRuntime.NeedsContainer(space.Runtime);

        try
        {
            if (wasRunning)
                _spaces.Power(uuid, "stop");
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"transfer stop {uuid}: {ex.Message}");
        }

        var fsPath = _spaces.EffectiveFsPath(uuid);
        var tmp = Path.Combine(_config.System.TmpDirectory, $"transfer-{uuid}.tar.gz");
        var stageDir = Path.Combine(_config.System.TmpDirectory, $"transfer-stage-{uuid:N}");
        Directory.CreateDirectory(_config.System.TmpDirectory);

        try
        {
            _progress.MarkRunning(uuid, "outgoing", includeBackups ? "Packing files and backups…" : "Packing files…");
            if (includeBackups)
            {
                PrepareTransferStage(uuid, fsPath, stageDir);
                CreateTarGz(stageDir, tmp);
            }
            else
            {
                CreateTarGz(fsPath, tmp);
            }

            _progress.MarkRunning(uuid, "outgoing", "Uploading archive to destination…");
            await using var file = File.OpenRead(tmp);
            using var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            request.Headers.TryAddWithoutValidation("X-WebSpace-Uuid", uuid.ToString());
            request.Headers.TryAddWithoutValidation("X-Start-On-Completion", startOnCompletion ? "1" : "0");
            request.Headers.TryAddWithoutValidation("X-Include-Backups", includeBackups ? "1" : "0");
            request.Content = content;

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Destination rejected transfer: {(int)response.StatusCode} {body}");

            _spaces.Delete(uuid);
            try
            {
                await _panel.ReportTransferAsync(uuid, successful: true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"transfer report ok failed: {ex.Message}");
            }

            _progress.MarkCompleted(uuid, "outgoing");
            _logger?.Info(LoggerTypes.WebSpaces, $"Outgoing transfer completed for {uuid}");
        }
        catch (Exception ex)
        {
            _progress.MarkFailed(uuid, "outgoing", ex.Message);
            try
            {
                await _panel.ReportTransferAsync(uuid, successful: false, cancellationToken);
            }
            catch { /* ignore */ }

            throw new InvalidOperationException($"Outgoing transfer failed: {ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); } catch { /* ignore */ }
        }
    
    }

    /// <summary>Accept archive on this node; extract and register WebSpace from panel config.</summary>
    public Task IncomingAsync(
        Guid uuid,
        Stream archive,
        bool startOnCompletion,
        CancellationToken cancellationToken = default) =>
        _events.WithHooksAsync(
            new TransferIncomingBeforeEvent { WebSpaceUuid = uuid, StartOnCompletion = startOnCompletion },
            err => new TransferIncomingAfterEvent { WebSpaceUuid = uuid, Error = err },
            token => IncomingCoreAsync(uuid, archive, startOnCompletion, token),
            cancellationToken);

    private async Task IncomingCoreAsync(
        Guid uuid,
        Stream archive,
        bool startOnCompletion,
        CancellationToken cancellationToken)
    {
        _progress.MarkRunning(uuid, "incoming", "Receiving archive…");
        try
        {
            if (_spaces.Get(uuid) is not null)
                throw new InvalidOperationException($"WebSpace {uuid} already exists on this node.");

            _spaces.CreateFromPanel(new CreateWebSpaceRequest
            {
                Uuid = uuid,
                SkipScripts = true,
                StartOnCompletion = false,
            });

            var fsPath = _spaces.EffectiveFsPath(uuid);
            WipeContentsKeepMeta(fsPath);
            _progress.MarkRunning(uuid, "incoming", "Extracting archive…");
            await ExtractTarGzAsync(archive, fsPath, cancellationToken);
            PromoteBundledBackups(uuid, fsPath);

            if (startOnCompletion)
            {
                try { _spaces.Power(uuid, "start"); }
                catch (Exception ex)
                {
                    _logger?.Warning(LoggerTypes.WebSpaces, $"transfer start {uuid}: {ex.Message}");
                }
            }

            _progress.MarkCompleted(uuid, "incoming");
            _logger?.Info(LoggerTypes.WebSpaces, $"Incoming transfer accepted for {uuid}");
        }
        catch (Exception ex)
        {
            _progress.MarkFailed(uuid, "incoming", ex.Message);
            throw;
        }
    
    }

    /// <summary>Stage WebSpace files plus optional local backup sidecar under <c>__quilld_backups__</c>.</summary>
    private void PrepareTransferStage(Guid uuid, string fsPath, string stageDir)
    {
        if (Directory.Exists(stageDir))
            Directory.Delete(stageDir, recursive: true);
        Directory.CreateDirectory(stageDir);

        foreach (var entry in Directory.EnumerateFileSystemEntries(fsPath))
        {
            var name = Path.GetFileName(entry);
            var dest = Path.Combine(stageDir, name);
            if (Directory.Exists(entry))
                CopyDirectory(entry, dest);
            else
                File.Copy(entry, dest, overwrite: true);
        }

        var backupSrc = Path.Combine(_config.System.BackupDirectory, uuid.ToString("D"));
        if (!Directory.Exists(backupSrc))
            return;

        var backupDest = Path.Combine(stageDir, BackupsBundleDir);
        CopyDirectory(backupSrc, backupDest);
    }

    /// <summary>Move transferred <c>__quilld_backups__</c> into BackupDirectory/{uuid}.</summary>
    private void PromoteBundledBackups(Guid uuid, string fsPath)
    {
        var bundled = Path.Combine(fsPath, BackupsBundleDir);
        if (!Directory.Exists(bundled))
            return;

        var dest = Path.Combine(_config.System.BackupDirectory, uuid.ToString("D"));
        Directory.CreateDirectory(_config.System.BackupDirectory);
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        Directory.Move(bundled, dest);
        _logger?.Info(LoggerTypes.WebSpaces, $"Promoted transferred backups for {uuid}");
    }

    private const string BackupsBundleDir = "__quilld_backups__";

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private static void CreateTarGz(string sourceDir, string archivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using var file = File.Create(archivePath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        TarFile.CreateFromDirectory(sourceDir, gzip, includeBaseDirectory: false);
    }

    private async Task ExtractTarGzAsync(Stream archive, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var tmp = Path.Combine(_config.System.TmpDirectory, $"quilld-in-{Guid.NewGuid():N}.tar.gz");
        Directory.CreateDirectory(_config.System.TmpDirectory);
        try
        {
            await using (var fs = File.Create(tmp))
            {
                await archive.CopyToAsync(fs, ct);
            }

            await using var file = File.OpenRead(tmp);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destDir, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static void WipeContentsKeepMeta(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (name is "webspace.json" or "site.json")
                continue;
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
            catch { /* best-effort */ }
        }
    }
}
