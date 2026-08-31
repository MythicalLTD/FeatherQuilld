using System.Text.Json.Serialization;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FeatherQuilld.Controllers;

[Tags("Files")]
[Authorize]
[ApiController]
[Route("api/webspaces/{uuid:guid}/files")]
public sealed class WebSpaceFilesController : ControllerBase
{
    private readonly WebSpaceFileService _files;
    private readonly WebSpaceTrashService _trash;
    private readonly WebSpacePullJobStore _pullJobs;
    private readonly AppConfig _config;

    public WebSpaceFilesController(
        WebSpaceFileService files,
        WebSpaceTrashService trash,
        WebSpacePullJobStore pullJobs,
        AppConfig config)
    {
        _files = files;
        _trash = trash;
        _pullJobs = pullJobs;
        _config = config;
    }

    /// <summary>List files and directories under a path.</summary>
    [HttpGet("list")]
    public IActionResult List(Guid uuid, [FromQuery] string? directory = "/")
    {
        try { return Ok(new { data = _files.List(uuid, directory) }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpGet("contents")]
    public IActionResult Contents(Guid uuid, [FromQuery] string file)
    {
        try { return Content(_files.ReadText(uuid, file), "text/plain; charset=utf-8"); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("write")]
    public async Task<IActionResult> Write(Guid uuid, [FromQuery] string file, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            _files.WriteText(uuid, file, body);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("create-directory")]
    public IActionResult CreateDirectory(Guid uuid, [FromBody] CreateDirBody body)
    {
        try
        {
            _files.CreateDirectory(uuid, body.Name ?? body.Directory ?? "/");
            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("rename")]
    public IActionResult Rename(Guid uuid, [FromBody] RenameBody body)
    {
        try
        {
            _files.Rename(uuid, body.From ?? "", body.To ?? "");
            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("copy")]
    [EnableRateLimiting("expensive")]
    public IActionResult Copy(Guid uuid, [FromBody] CopyBody body)
    {
        try
        {
            var path = _files.Copy(uuid, body.From ?? body.File ?? "", body.To ?? body.Destination);
            return Ok(new { ok = true, data = new { path } });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Copy multiple files into a destination directory.</summary>
    [HttpPost("copy-many")]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult CopyMany(Guid uuid, [FromBody] CopyManyBody body)
    {
        try
        {
            var files = _files.CopyMany(uuid, body.Files ?? [], body.Destination);
            return Ok(new { ok = true, data = new { files } });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Create a symbolic link within the WebSpace filesystem.</summary>
    [HttpPost("create-symlink")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult CreateSymlink(Guid uuid, [FromBody] CreateSymlinkBody body)
    {
        try
        {
            _files.CreateSymlink(uuid, body.Link ?? "", body.Target ?? "");
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Compute content fingerprints (hashes) for selected files.</summary>
    [HttpGet("fingerprints")]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Fingerprints(
        Guid uuid,
        [FromQuery] string algorithm = "sha256",
        [FromQuery] List<string>? files = null)
    {
        try
        {
            var hashed = _files.Fingerprints(uuid, files ?? [], algorithm);
            return Ok(new { files = hashed });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("delete")]
    public IActionResult Delete(Guid uuid, [FromBody] DeleteBody body)
    {
        try
        {
            var permanent = body.Permanent == true;
            var useTrash = body.UseTrash != false && !permanent;
            _files.Delete(uuid, body.Files ?? [], useTrash, permanent);
            return Ok(new { ok = true, trash = useTrash && !permanent });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("trash")]
    public IActionResult ListTrash(
        Guid uuid,
        [FromQuery] long maxSizeBytes = 5L * 1024 * 1024 * 1024,
        [FromQuery] int retentionDays = 30)
    {
        try
        {
            var result = _trash.ListTrash(uuid, maxSizeBytes, retentionDays);
            return Ok(new
            {
                entries = result.Entries.Select(e => new
                {
                    id = e.Id,
                    original_root = e.OriginalRoot,
                    original_name = e.OriginalName,
                    deleted_at = e.DeletedAt,
                    size = e.Size,
                    is_directory = e.IsDirectory,
                }),
                total_size = result.TotalSize,
            });
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("trash/restore")]
    public IActionResult RestoreTrash(Guid uuid, [FromBody] TrashRestoreBody body)
    {
        try
        {
            _trash.RestoreTrash(uuid, body.Ids ?? [], body.Overwrite == true);
            return Ok(new { ok = true });
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("trash/delete")]
    public IActionResult DeleteTrash(Guid uuid, [FromBody] TrashIdsBody body)
    {
        try
        {
            _trash.DeleteTrashEntries(uuid, body.Ids ?? []);
            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("trash/empty")]
    public IActionResult EmptyTrash(Guid uuid)
    {
        try
        {
            _trash.EmptyTrash(uuid);
            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("download")]
    public IActionResult Download(Guid uuid, [FromQuery] string file)
    {
        try
        {
            var stream = _files.OpenRead(uuid, file);
            var name = Path.GetFileName(file);
            return File(stream, "application/octet-stream", name);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("upload")]
    [EnableRateLimiting("expensive")]
    [RequestSizeLimit(1024L * 1024L * 512L)]
    public async Task<IActionResult> Upload(Guid uuid, [FromQuery] string? directory, CancellationToken ct)
    {
        try
        {
            if (Request.Form.Files.Count == 0)
                return BadRequest(new { error = "No files uploaded." });

            foreach (var formFile in Request.Form.Files)
            {
                await using var stream = formFile.OpenReadStream();
                await _files.UploadAsync(uuid, directory ?? "/", formFile.FileName, stream, ct);
            }

            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Compress selected paths into an archive.</summary>
    [HttpPost("compress")]
    [EnableRateLimiting("expensive")]
    public IActionResult Compress(Guid uuid, [FromBody] CompressBody body)
    {
        try
        {
            var path = _files.Compress(
                uuid,
                body.Root ?? body.Directory ?? "/",
                body.Files ?? [],
                body.Name,
                body.Extension ?? "tar.gz");
            return Ok(new { ok = true, data = new { path } });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("decompress")]
    [EnableRateLimiting("expensive")]
    public IActionResult Decompress(Guid uuid, [FromBody] DecompressBody body)
    {
        try
        {
            var file = body.File ?? "";
            if (!string.IsNullOrWhiteSpace(body.Root)
                && !string.IsNullOrWhiteSpace(file)
                && !file.Contains('/')
                && file is not ("/" or "."))
            {
                var root = body.Root!.TrimEnd('/');
                file = root is "" or "/" ? "/" + file : root + "/" + file;
            }
            else if (string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(body.Root))
            {
                file = body.Root!;
            }
            _files.Decompress(uuid, file);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("chmod")]
    public IActionResult Chmod(Guid uuid, [FromBody] ChmodBody body)
    {
        try
        {
            var entries = (body.Files ?? [])
                .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.File))
                .Select(f => (f!.File!, f.Mode ?? "0644"))
                .ToList();
            _files.Chmod(uuid, entries);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Search file names under a directory.</summary>
    [HttpGet("search")]
    public IActionResult Search(
        Guid uuid,
        [FromQuery] string query,
        [FromQuery] string? directory = "/",
        [FromQuery] int limit = 100)
    {
        try
        {
            return Ok(new { data = _files.Search(uuid, directory, query, limit) });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("pull")]
    [EnableRateLimiting("expensive")]
    public async Task<IActionResult> Pull(Guid uuid, [FromBody] PullBody body, CancellationToken ct)
    {
        try
        {
            var directory = body.Directory ?? body.Root ?? "/";
            var url = body.Url ?? "";
            var maxBytes = body.MaxBytes > 0 ? body.MaxBytes : WebSpaceFileService.DefaultPullMaxBytes;

            if (body.Background == true)
            {
                var id = _pullJobs.StartPull(uuid, directory, url, body.FileName ?? body.Filename, maxBytes);
                return Accepted(new { identifier = id, background = true });
            }

            var path = await _files.PullAsync(
                uuid,
                directory,
                url,
                body.FileName ?? body.Filename,
                maxBytes,
                ct);
            return Ok(new { ok = true, data = new { path } });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("pull-jobs")]
    public IActionResult ListPullJobs(Guid uuid)
    {
        try { return Ok(new { data = _pullJobs.ListFor(uuid) }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("pull-jobs/{identifier}")]
    public IActionResult CancelPullJob(Guid uuid, string identifier)
    {
        try
        {
            if (!_pullJobs.Cancel(uuid, identifier))
                return NotFound(new { error = "Pull job not found." });
            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("download-directory")]
    [EnableRateLimiting("expensive")]
    public IActionResult DownloadDirectory(
        Guid uuid,
        [FromQuery] string directory,
        [FromQuery] string format = "tar.gz")
    {
        try
        {
            var temp = _files.CreateDirectoryDownloadArchive(uuid, directory, format);
            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
            var ext = format.Trim().Equals("zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";
            var name = (Path.GetFileName(directory.TrimEnd('/')) is { Length: > 0 } n ? n : "download") + "." + ext;
            return File(stream, "application/octet-stream", name);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("archive-list")]
    public IActionResult ListArchive(
        Guid uuid,
        [FromQuery] string? directory = "/",
        [FromQuery] string? file = null,
        [FromQuery(Name = "archive_path")] string? archivePath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file))
                return BadRequest(new { error = "file is required." });
            var result = _files.ListArchiveDirectory(uuid, directory ?? "/", file, archivePath);
            return Ok(new { contents = result.Contents, truncated = result.Truncated });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("extract-archive-selection")]
    [EnableRateLimiting("expensive")]
    public IActionResult ExtractArchiveSelection(Guid uuid, [FromBody] ExtractArchiveBody body)
    {
        try
        {
            _files.ExtractArchiveSelection(
                uuid,
                body.Root ?? "/",
                body.File ?? "",
                body.Destination ?? "/",
                body.Entries ?? []);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("search-advanced")]
    public IActionResult SearchAdvanced(
        Guid uuid,
        [FromQuery] string? directory = "/",
        [FromQuery] string? pattern = null,
        [FromQuery] string? include = null,
        [FromQuery] string? exclude = null,
        [FromQuery] bool case_insensitive = true,
        [FromQuery] string? content = null,
        [FromQuery] bool content_case_insensitive = true,
        [FromQuery] long? min_size = null,
        [FromQuery] long? max_size = null,
        [FromQuery] int limit = 100)
    {
        try
        {
            var options = new WebSpaceFileService.AdvancedSearchOptions
            {
                Pattern = pattern,
                Include = include,
                Exclude = exclude,
                CaseInsensitive = case_insensitive,
                Content = content,
                ContentCaseInsensitive = content_case_insensitive,
                MinSize = min_size,
                MaxSize = max_size,
            };
            return Ok(new { data = _files.SearchAdvanced(uuid, directory, options, limit) });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("wipe")]
    [EnableRateLimiting("expensive")]
    public IActionResult Wipe(Guid uuid, [FromBody] WipeBody body)
    {
        try
        {
            if (!string.Equals(body.Confirm, "WIPE", StringComparison.Ordinal))
                return BadRequest(new { error = "confirm must be WIPE" });
            _files.WipeAll(uuid);
            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("upload-signed")]
    [EnableRateLimiting("expensive")]
    [RequestSizeLimit(1024L * 1024L * 512L)]
    [AllowAnonymous]
    public async Task<IActionResult> UploadSigned(
        Guid uuid,
        [FromQuery] string token,
        CancellationToken ct)
    {
        try
        {
            if (!WebSpaceUploadToken.TryValidate(token, uuid, out var payload)
                || !WebSpaceUploadToken.ValidateSignature(_config, token))
                return Unauthorized(new { error = "Invalid or expired upload token." });

            if (Request.Form.Files.Count == 0)
                return BadRequest(new { error = "No files uploaded." });

            foreach (var formFile in Request.Form.Files)
            {
                await using var stream = formFile.OpenReadStream();
                var name = string.IsNullOrWhiteSpace(payload.FileName) ? formFile.FileName : payload.FileName;
                await _files.UploadAsync(uuid, payload.Directory, name, stream, ct);
            }

            return Ok(new { ok = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    public sealed class CreateDirBody
    {
        public string? Name { get; set; }
        public string? Directory { get; set; }
    }

    public sealed class RenameBody
    {
        public string? From { get; set; }
        public string? To { get; set; }
    }

    public sealed class CopyBody
    {
        public string? From { get; set; }
        public string? File { get; set; }
        public string? To { get; set; }
        public string? Destination { get; set; }
    }

    public sealed class CopyManyBody
    {
        public List<string>? Files { get; set; }
        public string? Destination { get; set; }
    }

    public sealed class CreateSymlinkBody
    {
        public string? Link { get; set; }
        public string? Target { get; set; }
    }

    public sealed class DeleteBody
    {
        public List<string>? Files { get; set; }
        public bool? Permanent { get; set; }

        [JsonPropertyName("use_trash")]
        public bool? UseTrash { get; set; }
    }

    public sealed class TrashRestoreBody
    {
        public List<string>? Ids { get; set; }
        public bool? Overwrite { get; set; }
    }

    public sealed class TrashIdsBody
    {
        public List<string>? Ids { get; set; }
    }

    public sealed class CompressBody
    {
        public string? Root { get; set; }
        public string? Directory { get; set; }
        public List<string>? Files { get; set; }
        public string? Name { get; set; }
        public string? Extension { get; set; }
    }

    public sealed class DecompressBody
    {
        public string? File { get; set; }
        public string? Root { get; set; }
    }

    public sealed class ChmodBody
    {
        public List<ChmodEntry>? Files { get; set; }
    }

    public sealed class ChmodEntry
    {
        public string? File { get; set; }
        public string? Mode { get; set; }
    }

    public sealed class PullBody
    {
        public string? Url { get; set; }
        public string? Directory { get; set; }
        public string? Root { get; set; }

        [JsonPropertyName("file_name")]
        public string? FileName { get; set; }

        public string? Filename { get; set; }
        public long MaxBytes { get; set; }
        public bool? Background { get; set; }
    }

    public sealed class ExtractArchiveBody
    {
        public string? Root { get; set; }
        public string? File { get; set; }
        public string? Destination { get; set; }
        public List<string>? Entries { get; set; }
    }

    public sealed class WipeBody
    {
        public string? Confirm { get; set; }
    }
}
