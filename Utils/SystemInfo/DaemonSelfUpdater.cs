using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherQuilld.Commands;
using FeatherQuilld.Utils.Startup;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.SystemInfo;

/// <summary>Download and replace the running FeatherQuilld binary (Linux).</summary>
public sealed class DaemonSelfUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static DaemonSelfUpdater()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("FeatherQuilld", StartupBanner.Version));
    }

    public sealed record SelfUpdateRequest(
        string Source = "github",
        string? RepoOwner = null,
        string? RepoName = null,
        string? Version = null,
        string? Url = null,
        string? Sha256 = null,
        bool Force = false,
        bool DisableChecksum = false);

    public sealed record SelfUpdateResult(bool Success, string Message, bool RestartScheduled = false)
    {
        public static SelfUpdateResult Ok(string message, bool restartScheduled = false) =>
            new(true, message, restartScheduled);

        public static SelfUpdateResult Fail(string message) => new(false, message, false);
    }

    public static async Task<SelfUpdateResult> ApplyAsync(
        SelfUpdateRequest request,
        AppLogger? logger,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return SelfUpdateResult.Fail("Self-update is only supported on Linux.");

        var target = SystemdServiceInstaller.ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            return SelfUpdateResult.Fail("Could not locate the running FeatherQuilld binary.");

        if (Path.GetFileName(target).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return SelfUpdateResult.Fail("Self-update requires a published binary, not dotnet run.");

        try
        {
            var (downloadUrl, expectedSha256) = await ResolveDownloadAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return SelfUpdateResult.Fail("Could not resolve a download URL for the update.");

            logger?.Info(LoggerTypes.Application, $"Self-update downloading from {downloadUrl}");

            var tempDir = Path.Combine(Path.GetTempPath(), "featherquilld-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var downloadPath = Path.Combine(tempDir, "FeatherQuilld.new");

            try
            {
                await DownloadFileAsync(downloadUrl, downloadPath, ct).ConfigureAwait(false);
                TryMarkExecutable(downloadPath);

                var sha256 = request.DisableChecksum
                    ? null
                    : (expectedSha256 ?? request.Sha256)?.Trim();
                if (!string.IsNullOrWhiteSpace(sha256))
                {
                    var actual = ComputeSha256Hex(downloadPath);
                    if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        return SelfUpdateResult.Fail($"Checksum mismatch (expected {sha256}, got {actual}).");
                }

                if (!request.Force && string.Equals(StartupBanner.Version.TrimStart('v'),
                        await TryReadVersionFromBinaryAsync(downloadPath, ct).ConfigureAwait(false),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return SelfUpdateResult.Fail("Downloaded binary matches the current version. Pass force=true to reinstall.");
                }

                var stagingPath = target + ".new";
                File.Copy(downloadPath, stagingPath, overwrite: true);
                TryMarkExecutable(stagingPath);

                var restarted = ScheduleReplaceAndRestart(target, stagingPath, logger);
                return SelfUpdateResult.Ok(
                    restarted
                        ? "Update staged — FeatherQuilld will restart shortly."
                        : "Update staged — restart featherquilld manually to apply.",
                    restarted);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Self-update failed: {ex.Message}");
            return SelfUpdateResult.Fail(ex.Message);
        }
    }

    private static async Task<(string? Url, string? Sha256)> ResolveDownloadAsync(
        SelfUpdateRequest request,
        CancellationToken ct)
    {
        if (string.Equals(request.Source, "url", StringComparison.OrdinalIgnoreCase))
            return (request.Url?.Trim(), request.Sha256?.Trim());

        var owner = string.IsNullOrWhiteSpace(request.RepoOwner) ? "mythicalltd" : request.RepoOwner.Trim();
        var repo = string.IsNullOrWhiteSpace(request.RepoName) ? "featherquilld" : request.RepoName.Trim();
        var version = request.Version?.Trim().TrimStart('v');

        var releaseUrl = string.IsNullOrWhiteSpace(version)
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/latest"
            : $"https://api.github.com/repos/{owner}/{repo}/releases/tags/v{version.TrimStart('v')}";

        using var response = await Http.GetAsync(releaseUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub release lookup failed ({(int)response.StatusCode}).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException("Unsupported CPU architecture for self-update."),
        };

        var assets = doc.RootElement.GetProperty("assets");
        string? bestUrl = null;
        string? bestSha = null;
        var score = -1;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var lower = name.ToLowerInvariant();
            if (!lower.Contains("linux") && !lower.Contains("FeatherQuilld".ToLowerInvariant()) && !lower.Contains(repo))
                continue;

            var candidateScore = 0;
            if (lower.Contains(arch) || lower.Contains(arch switch { "x64" => "amd64", _ => "arm64" }))
                candidateScore += 4;
            if (lower.Contains("linux"))
                candidateScore += 2;
            if (lower.Contains(repo) || lower.Contains("featherquilld"))
                candidateScore += 1;
            if (lower.EndsWith(".tar.gz") || lower.EndsWith(".zip"))
                candidateScore -= 1;

            if (candidateScore > score)
            {
                score = candidateScore;
                bestUrl = url;
                bestSha = null;
            }
        }

        if (bestUrl is null)
        {
            // Fallback: releases/latest/download/{repo} or FeatherQuilld
            foreach (var candidate in new[] { repo, "FeatherQuilld", "featherquilld" })
            {
                var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "latest";
                var url = $"https://github.com/{owner}/{repo}/releases/download/v{tag}/{candidate}";
                if (await HeadOkAsync(url, ct).ConfigureAwait(false))
                    return (url, null);
            }
        }

        return (bestUrl, bestSha);
    }

    private static async Task<bool> HeadOkAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var local = File.Create(destPath);
        await remote.CopyToAsync(local, ct).ConfigureAwait(false);
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string?> TryReadVersionFromBinaryAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = (await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            if (output.Length == 0)
                output = (await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().TrimStart('v');
        }
        catch
        {
            return null;
        }
    }

    private static bool ScheduleReplaceAndRestart(string target, string stagingPath, AppLogger? logger)
    {
        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"featherquilld-restart-{Guid.NewGuid():N}.sh");
            var script = new StringBuilder();
            script.AppendLine("#!/bin/bash");
            script.AppendLine("set -e");
            script.AppendLine("sleep 2");
            script.AppendLine($"install -m 755 {Quote(stagingPath)} {Quote(target)}");
            script.AppendLine($"rm -f {Quote(stagingPath)}");
            script.AppendLine("if systemctl is-active --quiet featherquilld 2>/dev/null; then");
            script.AppendLine("  systemctl restart featherquilld");
            script.AppendLine("else");
            script.AppendLine($"  nohup {Quote(target)} >/dev/null 2>&1 &");
            script.AppendLine("fi");
            script.AppendLine($"rm -f {Quote(scriptPath)}");
            File.WriteAllText(scriptPath, script.ToString());
            TryMarkExecutable(scriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { scriptPath },
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warning(LoggerTypes.Application, $"Self-update restart script failed: {ex.Message}");
            return false;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void TryMarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // best-effort
        }
    }
}
