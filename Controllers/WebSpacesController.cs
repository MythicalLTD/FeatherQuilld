using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherQuilld.Utils.Auth;
using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FeatherQuilld.Controllers;

/// <summary>
/// WebSpaces on this node. Create is Wings-style: panel sends uuid; daemon pulls config/install from panel.
/// </summary>
[Tags("WebSpaces")]
[Authorize]
[ApiController]
[Route("api/webspaces")]
[Produces("application/json")]
public sealed class WebSpacesController : ControllerBase
{
    private static readonly JsonSerializerOptions WsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly WebSpaceStore _spaces;
    private readonly ConsoleJwtValidator _consoleJwt;

    public WebSpacesController(WebSpaceStore spaces, ConsoleJwtValidator consoleJwt)
    {
        _spaces = spaces;
        _consoleJwt = consoleJwt;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WebSpaceResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<WebSpaceResponse>> List() =>
        Ok(_spaces.List().Select(_spaces.ToResponse).ToList());

    [HttpGet("{uuid:guid}")]
    [ProducesResponseType(typeof(WebSpaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WebSpaceResponse> Get(Guid uuid)
    {
        var space = _spaces.Get(uuid);
        if (space is null)
            return NotFound(new { error = "WebSpace not found." });

        return Ok(_spaces.ToResponse(space));
    }

    [HttpGet("{uuid:guid}/status")]
    [ProducesResponseType(typeof(WebSpaceStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WebSpaceStatusResponse> Status(Guid uuid)
    {
        try
        {
            return Ok(_spaces.Status(uuid));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "WebSpace not found." });
        }
    }

    [HttpGet("{uuid:guid}/logs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Logs(Guid uuid, [FromQuery] int lines = 100)
    {
        try
        {
            var text = _spaces.GetRuntimeLogs(uuid, lines);
            return Ok(new { data = text });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("{uuid:guid}/logs/install")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult InstallLogs(Guid uuid)
    {
        try
        {
            var text = _spaces.GetInstallLogs(uuid);
            return Ok(new { data = text });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "WebSpace not found." });
        }
    }

    [HttpGet("{uuid:guid}/ssl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Ssl(Guid uuid)
    {
        try
        {
            return Ok(_spaces.GetSslStatus(uuid));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "WebSpace not found." });
        }
    }

    [HttpPost("{uuid:guid}/ssl/renew")]
    [EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenewSsl(Guid uuid, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _spaces.RenewSslAsync(uuid, cancellationToken);
            return Ok(status);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Interactive console WebSocket. Browser clients auth via first message
    /// <c>{"event":"auth","args":["&lt;jwt&gt;"]}</c>; panel proxies may pass node bearer via
    /// Authorization / <c>?token=</c> and skip the JWT event. After auth, clients may send
    /// <c>{"event":"send command","args":["&lt;line&gt;"]}</c>.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{uuid:guid}/ws")]
    public async Task ConsoleWs(Guid uuid, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Expected WebSocket upgrade." }, cancellationToken);
            return;
        }

        if (_spaces.Get(uuid) is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "WebSpace not found." }, cancellationToken);
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        IReadOnlyList<string> permissions = [ConsolePermissions.Wildcard];
        var bearerOk = User.Identity?.IsAuthenticated == true;
        if (!bearerOk)
        {
            var (ok, jwtPermissions) = await TryAuthenticateConsoleJwtAsync(socket, uuid, cancellationToken);
            if (!ok)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "auth failed",
                        CancellationToken.None);
                }

                return;
            }

            permissions = jwtPermissions;
        }

        if (!ConsolePermissions.Allows(permissions, ConsolePermissions.Output))
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "missing console.output",
                    CancellationToken.None);
            }

            return;
        }

        await SendWsEventAsync(socket, "auth success", Array.Empty<string>(), cancellationToken);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var followTask = FollowAndSendAsync(socket, uuid, linked.Token);
        var canSend = ConsolePermissions.Allows(permissions, ConsolePermissions.Send);
        var recvTask = ReceiveLoopAsync(socket, uuid, canSend, linked.Token);

        await Task.WhenAny(followTask, recvTask);
        linked.Cancel();
        try { await Task.WhenAll(followTask, recvTask); } catch { /* cancelled */ }

        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    private async Task<(bool Ok, IReadOnlyList<string> Permissions)> TryAuthenticateConsoleJwtAsync(
        WebSocket socket,
        Guid uuid,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        string? text;
        try
        {
            text = await ReceiveTextMessageAsync(socket, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return (false, Array.Empty<string>());
        }
        catch (WebSocketException)
        {
            return (false, Array.Empty<string>());
        }

        if (string.IsNullOrWhiteSpace(text))
            return (false, Array.Empty<string>());

        string? jwt = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("event", out var eventEl)
                ? eventEl.GetString()
                : null;
            if (!string.Equals(evt, "auth", StringComparison.OrdinalIgnoreCase))
                return (false, Array.Empty<string>());

            if (!root.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array)
                return (false, Array.Empty<string>());

            if (argsEl.GetArrayLength() < 1)
                return (false, Array.Empty<string>());

            jwt = argsEl[0].GetString();
        }
        catch (JsonException)
        {
            return (false, Array.Empty<string>());
        }

        if (!_consoleJwt.TryValidate(jwt ?? "", uuid, out _, out var permissions))
            return (false, Array.Empty<string>());

        return (true, permissions);
    }

    private static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        if (ms.Length == 0)
            return null;

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Panel create — body: <c>{ "uuid": "...", "start_on_completion": false, "skip_scripts": false }</c>.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WebSpaceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<WebSpaceResponse> Create([FromBody] CreateWebSpaceBody body)
    {
        try
        {
            var space = _spaces.CreateFromPanel(new CreateWebSpaceRequest
            {
                Uuid = body.Uuid,
                StartOnCompletion = body.StartOnCompletion,
                SkipScripts = body.SkipScripts,
            });

            return CreatedAtAction(nameof(Get), new { uuid = space.Uuid }, _spaces.ToResponse(space));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{uuid:guid}/power")]
    [ProducesResponseType(typeof(WebSpaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WebSpaceResponse> Power(Guid uuid, [FromBody] PowerWebSpaceBody body)
    {
        try
        {
            var space = _spaces.Power(uuid, body.Action ?? "");
            return Ok(_spaces.ToResponse(space));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{uuid:guid}/reinstall")]
    [ProducesResponseType(typeof(WebSpaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WebSpaceResponse> Reinstall(Guid uuid, [FromBody] ReinstallWebSpaceBody? body)
    {
        try
        {
            var space = _spaces.Reinstall(
                uuid,
                wipeFiles: body?.WipeFiles ?? true,
                startOnCompletion: body?.StartOnCompletion ?? false);
            return Ok(_spaces.ToResponse(space));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{uuid:guid}/sync")]
    [ProducesResponseType(typeof(WebSpaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<WebSpaceResponse> Sync(Guid uuid)
    {
        try
        {
            var space = _spaces.ApplyConfigFromPanel(uuid);
            return Ok(_spaces.ToResponse(space));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>List backups for a WebSpace.</summary>
    [HttpGet("{uuid:guid}/backups")]
    [Tags("Backups")]
    public IActionResult ListBackups(Guid uuid, [FromServices] WebSpaceBackupService backups)
    {
        try { return Ok(backups.List(uuid)); }
        catch (InvalidOperationException) { return NotFound(new { error = "WebSpace not found." }); }
    }

    /// <summary>Create a backup (sync or async job).</summary>
    [HttpPost("{uuid:guid}/backup")]
    [Tags("Backups")]
    [EnableRateLimiting("expensive")]
    public IActionResult CreateBackup(Guid uuid, [FromBody] CreateBackupBody? body, [FromServices] WebSpaceBackupService backups)
    {
        try
        {
            var async = body?.Async ?? true;
            if (async)
            {
                var job = backups.StartCreateAsync(uuid, stopDuringBackup: body?.StopDuringBackup ?? false);
                return Accepted(new
                {
                    job_id = job.JobId,
                    phase = job.Phase.ToString().ToLowerInvariant(),
                    operation = job.Operation,
                });
            }

            return Ok(backups.Create(uuid, stopDuringBackup: body?.StopDuringBackup ?? false));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get status of an async backup/restore job.</summary>
    [HttpGet("{uuid:guid}/backups/jobs/{jobId:guid}")]
    [Tags("Backups")]
    public IActionResult GetBackupJob(Guid uuid, Guid jobId, [FromServices] WebSpaceBackupService backups)
    {
        var job = backups.GetJob(jobId);
        if (job is null || job.WebSpaceUuid != uuid)
            return NotFound(new { error = "Backup job not found." });

        return Ok(new
        {
            job_id = job.JobId,
            webspace_uuid = job.WebSpaceUuid,
            operation = job.Operation,
            phase = job.Phase.ToString().ToLowerInvariant(),
            backup_uuid = job.BackupUuid,
            bytes = job.Bytes,
            checksum = job.Checksum,
            message = job.Message,
            updated_at = job.UpdatedAt,
        });
    }

    [HttpGet("{uuid:guid}/utilization")]
    public IActionResult Utilization(Guid uuid, [FromServices] WebSpaceUtilizationService utilization)
    {
        try
        {
            var stats = utilization.Get(uuid);
            return Ok(new
            {
                uuid = stats.Uuid,
                disk_limit_bytes = stats.DiskLimitBytes,
                disk_used_bytes = stats.DiskUsedBytes,
                cpu_percent = stats.CpuPercent,
                memory_used_bytes = stats.MemoryUsedBytes,
                memory_limit_bytes = stats.MemoryLimitBytes,
                network_rx_bytes = stats.NetworkRxBytes,
                network_tx_bytes = stats.NetworkTxBytes,
                state = stats.State,
            });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "WebSpace not found." });
        }
    }

    /// <summary>Delete a backup archive.</summary>
    [HttpDelete("{uuid:guid}/backups/{backupUuid:guid}")]
    [Tags("Backups")]
    public IActionResult DeleteBackup(Guid uuid, Guid backupUuid, [FromServices] WebSpaceBackupService backups)
    {
        try
        {
            return backups.Delete(uuid, backupUuid)
                ? NoContent()
                : NotFound(new { error = "Backup not found." });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "WebSpace not found." });
        }
    }

    /// <summary>Download a backup archive as gzip.</summary>
    [HttpGet("{uuid:guid}/backups/{backupUuid:guid}/download")]
    [Tags("Backups")]
    public async Task<IActionResult> DownloadBackup(
        Guid uuid,
        Guid backupUuid,
        [FromServices] WebSpaceBackupService backups,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = backups.OpenDownload(uuid, backupUuid);
            if (stream is null)
                return NotFound(new { error = "Backup not found." });
            return File(stream, "application/gzip", $"{backupUuid}.tar.gz");
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { error = "WebSpace not found." });
        }
    }

    /// <summary>Restore a backup into the WebSpace filesystem.</summary>
    [HttpPost("{uuid:guid}/backups/{backupUuid:guid}/restore")]
    [Tags("Backups")]
    [EnableRateLimiting("expensive")]
    public IActionResult RestoreBackup(
        Guid uuid,
        Guid backupUuid,
        [FromBody] RestoreBackupBody? body,
        [FromServices] WebSpaceBackupService backups)
    {
        try
        {
            var async = body?.Async ?? true;
            if (async)
            {
                var job = backups.StartRestoreAsync(uuid, backupUuid);
                return Accepted(new
                {
                    job_id = job.JobId,
                    phase = job.Phase.ToString().ToLowerInvariant(),
                    operation = job.Operation,
                });
            }

            backups.Restore(uuid, backupUuid);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Import an uploaded tar.gz archive as a new backup.</summary>
    [HttpPost("{uuid:guid}/backups/import")]
    [Tags("Backups")]
    [EnableRateLimiting("expensive")]
    [RequestSizeLimit(1024L * 1024L * 1024L * 5L)]
    public async Task<IActionResult> ImportBackup(
        Guid uuid,
        [FromServices] WebSpaceBackupService backups,
        CancellationToken cancellationToken)
    {
        Stream bodyStream;
        if (Request.HasFormContentType && Request.Form.Files.Count > 0)
        {
            var file = Request.Form.Files.GetFile("archive") ?? Request.Form.Files[0];
            bodyStream = file.OpenReadStream();
        }
        else
        {
            bodyStream = Request.Body;
        }

        try
        {
            await using (bodyStream)
            {
                var result = backups.Import(uuid, bodyStream);
                return Ok(result);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Rebuild local sidecar backup index from the remote provider (restic/PBS).</summary>
    [HttpPost("{uuid:guid}/backups/reconcile")]
    [Tags("Backups")]
    public async Task<IActionResult> ReconcileBackups(
        Guid uuid,
        [FromServices] WebSpaceBackupService backups,
        CancellationToken cancellationToken)
    {
        try
        {
            var count = await backups.ReconcileAsync(uuid, cancellationToken);
            return Ok(new { reconciled = count });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Outgoing transfer to another Quilld node.</summary>
    [HttpPost("{uuid:guid}/transfer")]
    [EnableRateLimiting("expensive")]
    public async Task<IActionResult> Transfer(
        Guid uuid,
        [FromBody] TransferWebSpaceBody? body,
        [FromServices] WebSpaceTransferService transfers,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Url) || string.IsNullOrWhiteSpace(body.Token))
            return BadRequest(new { error = "url and token are required." });

        try
        {
            await transfers.OutgoingAsync(
                uuid,
                body.Url.Trim(),
                body.Token.Trim(),
                startOnCompletion: body.StartOnCompletion,
                includeBackups: body.IncludeBackups,
                cancellationToken);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{uuid:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(Guid uuid) =>
        _spaces.Delete(uuid) ? NoContent() : NotFound(new { error = "WebSpace not found." });

    private async Task FollowAndSendAsync(WebSocket socket, Guid uuid, CancellationToken ct)
    {
        try
        {
            await foreach (var line in _spaces.FollowRuntimeLogsAsync(uuid, sinceLines: 100, ct))
            {
                if (socket.State != WebSocketState.Open)
                    break;

                await SendWsEventAsync(socket, "console output", [line], ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException ex)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await SendWsEventAsync(socket, "console output", [ex.Message], CancellationToken.None);
                }
                catch { /* ignore */ }
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, Guid uuid, bool canSend, CancellationToken ct)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = await ReceiveTextMessageAsync(socket, ct);
                if (text is null)
                    break;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (!TryParseSendCommand(text, out var command))
                    continue;

                if (!canSend)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendWsEventAsync(
                                socket,
                                "console output",
                                ["Permission denied: console.send required."],
                                CancellationToken.None);
                        }
                        catch { /* ignore */ }
                    }

                    continue;
                }

                try
                {
                    await _spaces.SendConsoleCommandAsync(uuid, command, ct);
                }
                catch (InvalidOperationException ex)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendWsEventAsync(socket, "console output", [ex.Message], CancellationToken.None);
                        }
                        catch { /* ignore */ }
                    }
                }
                catch (Exception ex)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendWsEventAsync(
                                socket,
                                "console output",
                                [$"Failed to send command: {ex.Message}"],
                                CancellationToken.None);
                        }
                        catch { /* ignore */ }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    /// <summary>Parse Wings-shaped <c>send command</c> events. Returns false for other events.</summary>
    internal static bool TryParseSendCommand(string json, out string command)
    {
        command = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("event", out var eventEl)
                ? eventEl.GetString()
                : null;
            if (!string.Equals(evt, "send command", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(evt, "send_command", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!root.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array)
                return false;
            if (argsEl.GetArrayLength() < 1)
                return false;

            command = argsEl[0].GetString() ?? "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task SendWsEventAsync(
        WebSocket socket,
        string eventName,
        string[] args,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { Event = eventName, Args = args }, WsJson);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}

public sealed class CreateWebSpaceBody
{
    public Guid Uuid { get; set; }

    [JsonPropertyName("start_on_completion")]
    public bool StartOnCompletion { get; set; }

    [JsonPropertyName("skip_scripts")]
    public bool SkipScripts { get; set; }
}

public sealed class PowerWebSpaceBody
{
    public string? Action { get; set; }
}

public sealed class ReinstallWebSpaceBody
{
    [JsonPropertyName("wipe_files")]
    public bool WipeFiles { get; set; } = true;

    [JsonPropertyName("start_on_completion")]
    public bool StartOnCompletion { get; set; }
}

public sealed class CreateBackupBody
{
    [JsonPropertyName("stop_during_backup")]
    public bool StopDuringBackup { get; set; }

    [JsonPropertyName("async")]
    public bool Async { get; set; } = true;
}

public sealed class RestoreBackupBody
{
    [JsonPropertyName("async")]
    public bool Async { get; set; } = true;
}

public sealed class TransferWebSpaceBody
{
    /// <summary>Destination upload URL, e.g. https://dest:8989/api/transfers</summary>
    public string? Url { get; set; }

    /// <summary>Destination node bearer token (token_id.token).</summary>
    public string? Token { get; set; }

    [JsonPropertyName("start_on_completion")]
    public bool StartOnCompletion { get; set; } = true;

    [JsonPropertyName("include_backups")]
    public bool IncludeBackups { get; set; }

    [JsonPropertyName("stop_during_backup")]
    public bool StopDuringBackup { get; set; } = true;
}
