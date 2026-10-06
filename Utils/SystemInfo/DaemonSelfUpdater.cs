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

    internal const string AllowedOwner = "mythicalltd";
    internal const string AllowedRepo = "featherquilld";

    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

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

        if (request.DisableChecksum)
            return SelfUpdateResult.Fail("disable_checksum is not allowed.");

        var target = SystemdServiceInstaller.ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            return SelfUpdateResult.Fail("Could not locate the running FeatherQuilld binary.");

        if (Path.GetFileName(target).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return SelfUpdateResult.Fail("Self-update requires a published binary, not dotnet run.");

        try
        {
            var (downloadUrl, expectedSha256, releaseVersion) = await ResolveDownloadAsync(request, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloadUrl))
                return SelfUpdateResult.Fail("Could not resolve a download URL for the update.");

            if (!IsAllowedDownloadUrl(downloadUrl))
                return SelfUpdateResult.Fail("Download URL host is not allowlisted.");

            var sha256 = (expectedSha256 ?? request.Sha256)?.Trim();
            if (string.IsNullOrWhiteSpace(sha256))
                return SelfUpdateResult.Fail("SHA-256 checksum is required for self-update.");

            if (!request.Force)
            {
                var incoming = (request.Version ?? releaseVersion)?.Trim().TrimStart('v');
                var current = StartupBanner.Version.TrimStart('v');
                if (!string.IsNullOrWhiteSpace(incoming)
                    && string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase))
                {
                    return SelfUpdateResult.Fail(
                        "Requested version matches the current version. Pass force=true to reinstall.");
                }
            }

            logger?.Info(LoggerTypes.Application, $"Self-update downloading from {downloadUrl}");

            var tempDir = Path.Combine(Path.GetTempPath(), "featherquilld-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var downloadPath = Path.Combine(tempDir, "FeatherQuilld.new");

            try
            {
                await DownloadFileAsync(downloadUrl, downloadPath, ct).ConfigureAwait(false);
                TryMarkExecutable(downloadPath);

                var actual = ComputeSha256Hex(downloadPath);
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    return SelfUpdateResult.Fail($"Checksum mismatch (expected {sha256}, got {actual}).");

                if (!request.Force)
                {
                    var currentSha = ComputeSha256Hex(target);
                    if (currentSha.Equals(actual, StringComparison.OrdinalIgnoreCase))
                    {
                        return SelfUpdateResult.Fail(
                            "Downloaded binary matches the running binary. Pass force=true to reinstall.");
                    }
                }

                var stagingPath = target + ".new";
                File.Copy(downloadPath, stagingPath, overwrite: true);
                TryMarkExecutable(stagingPath);

                var restarted = ScheduleReplaceAndRestart(target, stagingPath, logger);
                return SelfUpdateResult.Ok(
                    restarted
                        ? "Update staged FeatherQuilld will restart shortly."
                        : "Update staged restart featherquilld manually to apply.",
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

    internal static bool IsAllowedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("https"))
            return false;
        return AllowedDownloadHosts.Contains(uri.Host);
    }

    internal static bool IsAllowedRepo(string? owner, string? repo)
    {
        var o = string.IsNullOrWhiteSpace(owner) ? AllowedOwner : owner.Trim();
        var r = string.IsNullOrWhiteSpace(repo) ? AllowedRepo : repo.Trim();
        return string.Equals(o, AllowedOwner, StringComparison.OrdinalIgnoreCase)
               && string.Equals(r, AllowedRepo, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string? Url, string? Sha256, string? Version)> ResolveDownloadAsync(
        SelfUpdateRequest request,
        CancellationToken ct)
    {
        if (string.Equals(request.Source, "url", StringComparison.OrdinalIgnoreCase))
        {
            var url = request.Url?.Trim();
            if (string.IsNullOrWhiteSpace(url) || !IsAllowedDownloadUrl(url))
                throw new InvalidOperationException("source=url requires an allowlisted https download URL.");
            if (string.IsNullOrWhiteSpace(request.Sha256))
                throw new InvalidOperationException("source=url requires sha256.");
            return (url, request.Sha256.Trim(), request.Version);
        }

        if (!IsAllowedRepo(request.RepoOwner, request.RepoName))
            throw new InvalidOperationException(
                $"Self-update is limited to {AllowedOwner}/{AllowedRepo}.");

        var owner = AllowedOwner;
        var repo = AllowedRepo;
        var version = request.Version?.Trim().TrimStart('v');

        var releaseUrl = string.IsNullOrWhiteSpace(version)
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/latest"
            : $"https://api.github.com/repos/{owner}/{repo}/releases/tags/v{version.TrimStart('v')}";

        using var response = await Http.GetAsync(releaseUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub release lookup failed ({(int)response.StatusCode}).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var tagName = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
            ? tagEl.GetString()?.Trim().TrimStart('v')
            : version;

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

        // Prefer a companion *.sha256 asset for the chosen binary when present.
        var shaByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".sha256.txt", StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(url) || !IsAllowedDownloadUrl(url))
                continue;
            try
            {
                var text = (await Http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
                var hex = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (hex is { Length: 64 } && hex.All(Uri.IsHexDigit))
                {
                    var stem = name
                        .Replace(".sha256.txt", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(".sha256", "", StringComparison.OrdinalIgnoreCase);
                    shaByStem[stem] = hex.ToLowerInvariant();
                }
            }
            catch
            {
                // ignore digest asset failures; request.Sha256 may still apply
            }
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(url) || !IsAllowedDownloadUrl(url))
                continue;

            var lower = name.ToLowerInvariant();
            if (lower.EndsWith(".sha256") || lower.EndsWith(".sha256.txt"))
                continue;
            if (!lower.Contains("linux") && !lower.Contains("featherquilld") && !lower.Contains(repo))
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
                bestSha = shaByStem.TryGetValue(name, out var matchedDigest) ? matchedDigest : null;
                if (bestSha is null)
                {
                    foreach (var (stem, stemDigest) in shaByStem)
                    {
                        if (name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                            || stem.StartsWith(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))
                        {
                            bestSha = stemDigest;
                            break;
                        }
                    }
                }
            }
        }

        var sha = bestSha ?? request.Sha256?.Trim();
        if (bestUrl is null)
            return (null, sha, tagName);

        return (bestUrl, sha, tagName);
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
