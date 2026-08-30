using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.WebSpaces;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Docker;

/// <summary>Wings-style install container: binds data dir + runs plate install script.</summary>
public sealed class WebSpaceInstaller
{
    private readonly DockerConfig _docker;
    private readonly WebSpaceWsHub? _wsHub;
    private readonly AppLogger? _logger;

    public WebSpaceInstaller(DockerConfig docker, AppLogger? logger = null, WebSpaceWsHub? wsHub = null)
    {
        _docker = docker;
        _logger = logger;
        _wsHub = wsHub;
    }

    public async Task RunAsync(
        WebSpace space,
        string dataPath,
        PanelInstallScript install,
        CancellationToken cancellationToken = default)
    {
        var script = install.Script?.Trim() ?? "";
        if (script.Length == 0)
        {
            _logger?.Info(LoggerTypes.WebSpaces,
                $"Install script empty for {space.Uuid} — skip installer container");
            return;
        }

        var image = string.IsNullOrWhiteSpace(install.ContainerImage)
            ? "alpine:3.20"
            : install.ContainerImage.Trim();
        var entrypoint = string.IsNullOrWhiteSpace(install.Entrypoint)
            ? "ash"
            : install.Entrypoint.Trim();

        var installDir = Path.Combine(dataPath, ".install");
        Directory.CreateDirectory(installDir);
        var scriptPath = Path.Combine(installDir, "install.sh");
        var logPath = Path.Combine(installDir, "install.log");
        await File.WriteAllTextAsync(scriptPath, NormalizeScript(script), cancellationToken);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best-effort on non-Unix.
        }

        var name = InstallerName(space.Uuid);
        using var client = DockerClientFactory.Create(_docker);

        await EnsureImageAsync(client, image, cancellationToken);
        await RemoveIfExistsAsync(client, name, cancellationToken);

        var memoryBytes = (long)_docker.InstallerLimits.Memory * 1024L * 1024L;
        var nanoCpus = (long)(_docker.InstallerLimits.Cpu / 100.0 * 1_000_000_000L);

        _logger?.Info(LoggerTypes.WebSpaces,
            $"Starting installer {name} image={image} entrypoint={entrypoint}");

        var create = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = image,
            Hostname = space.Uuid.ToString("N")[..12],
            Entrypoint = [entrypoint, "/mnt/install/install.sh"],
            Env = ["HOME=/root", "TERM=xterm"],
            HostConfig = new HostConfig
            {
                Binds =
                [
                    $"{dataPath}:/mnt/server",
                    $"{installDir}:/mnt/install:ro",
                ],
                AutoRemove = false,
                NetworkMode = "bridge",
                Memory = memoryBytes > 0 ? memoryBytes : 0,
                NanoCPUs = nanoCpus > 0 ? nanoCpus : 0,
                PidsLimit = _docker.ContainerPidLimit > 0 ? _docker.ContainerPidLimit : null,
            },
            WorkingDir = "/mnt/server",
        }, cancellationToken);

        var started = await client.Containers.StartContainerAsync(
            create.ID, new ContainerStartParameters(), cancellationToken);
        if (!started)
            throw new InvalidOperationException($"Failed to start installer container {name}.");

        using var logCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streamTask = StreamInstallerLogsAsync(client, create.ID, logPath, space.Uuid, logCts.Token);
        var wait = await client.Containers.WaitContainerAsync(create.ID, cancellationToken);
        logCts.Cancel();
        try { await streamTask.ConfigureAwait(false); } catch { /* cancelled when container exits */ }

        try
        {
            await client.Containers.RemoveContainerAsync(create.ID, new ContainerRemoveParameters
            {
                Force = true,
                RemoveVolumes = true,
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to remove installer {name}: {ex.Message}");
        }

        if (wait.StatusCode != 0)
            throw new InvalidOperationException($"Installer container exited with code {wait.StatusCode}.");

        _logger?.Info(LoggerTypes.WebSpaces, $"Installer finished ok for {space.Uuid}");
    }

    public static string InstallLogPath(string dataPath) =>
        Path.Combine(dataPath, ".install", "install.log");

    private async Task StreamInstallerLogsAsync(
        DockerClient client,
        string containerId,
        string logPath,
        Guid uuid,
        CancellationToken ct)
    {
        MultiplexedStream stream;
        try
        {
            stream = await client.Containers.GetContainerLogsAsync(
                containerId,
                tty: false,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true,
                    Timestamps = false,
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to follow installer logs: {ex.Message}");
            return;
        }

        await using var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        using (stream)
        {
            var buffer = new byte[8192];
            var leftover = "";
            while (!ct.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (result.EOF)
                    break;
                if (result.Count <= 0)
                    continue;

                var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                leftover += chunk;
                var parts = leftover.Split('\n');
                leftover = parts[^1];
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var line = parts[i] + "\n";
                    await logWriter.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
                    if (_wsHub is not null)
                        await _wsHub.SendInstallOutputAsync(uuid, line, ct).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrEmpty(leftover))
            {
                await logWriter.WriteAsync(leftover.AsMemory(), ct).ConfigureAwait(false);
                if (_wsHub is not null)
                    await _wsHub.SendInstallOutputAsync(uuid, leftover, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task CleanupAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = DockerClientFactory.Create(_docker);
            await RemoveIfExistsAsync(client, InstallerName(uuid), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"Installer cleanup {uuid}: {ex.Message}");
        }
    }

    public static string InstallerName(Guid uuid) => $"{uuid}_installer";

    private static string NormalizeScript(string script)
    {
        if (script.StartsWith("#!", StringComparison.Ordinal))
            return script.Replace("\r\n", "\n");
        return "#!/bin/sh\n" + script.Replace("\r\n", "\n");
    }

    private async Task EnsureImageAsync(DockerClient client, string image, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(image, ct);
            return;
        }
        catch (DockerImageNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        _logger?.Info(LoggerTypes.WebSpaces, $"Pulling Docker image {image}…");
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image },
            null,
            new Progress<JSONMessage>(),
            ct);
    }

    private static async Task RemoveIfExistsAsync(DockerClient client, string name, CancellationToken ct)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters
            {
                Force = true,
                RemoveVolumes = true,
            }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }
}
