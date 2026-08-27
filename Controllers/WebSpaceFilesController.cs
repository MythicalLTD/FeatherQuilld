using System.Text.Json.Serialization;
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

    public WebSpaceFilesController(WebSpaceFileService files) => _files = files;

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
            _files.Delete(uuid, body.Files ?? []);
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
            var path = await _files.PullAsync(
                uuid,
                body.Directory ?? body.Root ?? "/",
                body.Url ?? "",
                body.FileName ?? body.Filename,
                body.MaxBytes > 0 ? body.MaxBytes : WebSpaceFileService.DefaultPullMaxBytes,
                ct);
            return Ok(new { ok = true, data = new { path } });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
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
    }
}
