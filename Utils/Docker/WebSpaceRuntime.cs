using System.Collections.Concurrent;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.WebSpaces;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Docker;

/// <summary>Runtime Docker container for non-static WebSpaces.</summary>
public sealed class WebSpaceRuntime : IDisposable
{
    private readonly DockerConfig _docker;
    private readonly AppLogger? _logger;
    private readonly ConcurrentDictionary<Guid, StdinSession> _stdin = new();

    public WebSpaceRuntime(DockerConfig docker, AppLogger? logger = null)
    {
        _docker = docker;
        _logger = logger;
    }

    public static bool NeedsContainer(string runtime) =>
        !string.Equals(runtime, "static", StringComparison.OrdinalIgnoreCase);

    public static int DefaultContainerPort(string runtime, int platePort = 0)
    {
        if (platePort > 0)
            return platePort;

        return runtime.Trim().ToLowerInvariant() switch
        {
            "node" => 3000,
            "python" => 8000,
            "php" => 80,
            "custom" => 80,
            _ => 80,
        };
    }

    public static string MountTarget(string runtime) =>
        runtime.Trim().ToLowerInvariant() == "php" ? "/var/www/html" : "/home/container";

    public static string RuntimeName(Guid uuid) => uuid.ToString();

    public async Task StartAsync(
        WebSpace space,
        string dataPath,
        string? startup = null,
        CancellationToken cancellationToken = default)
    {
        if (!NeedsContainer(space.Runtime))
        {
            space.State = WebSpaceState.Running;
            return;
        }

        if (space.BackendPort <= 0)
            throw new InvalidOperationException("backend_port must be allocated before start.");

        var image = string.IsNullOrWhiteSpace(space.ContainerImage)
            ? throw new InvalidOperationException("No container image configured for this WebSpace.")
            : space.ContainerImage.Trim();

        var containerPort = space.ContainerPort > 0
            ? space.ContainerPort
            : DefaultContainerPort(space.Runtime);
        var name = RuntimeName(space.Uuid);
        var mount = MountTarget(space.Runtime);

        using var client = DockerClientFactory.Create(_docker);
        await EnsureImageAsync(client, image, cancellationToken);

        space.State = WebSpaceState.Starting;

        var existing = await TryInspectAsync(client, name, cancellationToken);
        if (existing is not null && existing.Config?.OpenStdin != true)
        {
            _logger?.Info(LoggerTypes.WebSpaces,
                $"Recreating runtime {name} to enable console stdin");
            ReleaseStdin(space.Uuid);
            try
            {
                await client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters
                {
                    Force = true,
                    RemoveVolumes = true,
                }, cancellationToken);
            }
            catch (DockerContainerNotFoundException)
            {
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }

            existing = null;
        }

        if (existing is not null)
        {
            if (existing.State?.Running == true)
            {
                space.State = WebSpaceState.Running;
                space.ContainerId = existing.ID;
                return;
            }

            await client.Containers.StartContainerAsync(name, new ContainerStartParameters(), cancellationToken);
            var after = await client.Containers.InspectContainerAsync(name, cancellationToken);
            space.ContainerId = after.ID;
            space.State = WebSpaceState.Running;
            _logger?.Info(LoggerTypes.WebSpaces, $"Started existing runtime {name}");
            return;
        }

        var env = new List<string>
        {
            "HOME=/home/container",
            "TERM=xterm",
            $"SERVER_UUID={space.Uuid}",
        };
        if (!string.IsNullOrWhiteSpace(startup))
            env.Add($"STARTUP={startup.Trim()}");
        if (!string.IsNullOrWhiteSpace(space.Startup))
            env.Add($"STARTUP={space.Startup.Trim()}");

        var portBindings = new Dictionary<string, IList<PortBinding>>
        {
            [$"{containerPort}/tcp"] =
            [
                new PortBinding
                {
                    HostIP = "127.0.0.1",
                    HostPort = space.BackendPort.ToString(),
                },
            ],
        };

        var create = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = image,
            Hostname = space.Uuid.ToString("N")[..12],
            Env = env,
            OpenStdin = true,
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            StdinOnce = false,
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                [$"{containerPort}/tcp"] = default,
            },
            HostConfig = new HostConfig
            {
                Binds = [$"{dataPath}:{mount}"],
                PortBindings = portBindings,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                NetworkMode = string.IsNullOrWhiteSpace(_docker.Network.NetworkMode)
                    ? "bridge"
                    : _docker.Network.NetworkMode,
                PidsLimit = _docker.ContainerPidLimit > 0 ? _docker.ContainerPidLimit : null,
                LogConfig = BuildLogConfig(),
            },
            WorkingDir = mount,
        }, cancellationToken);

        await client.Containers.StartContainerAsync(create.ID, new ContainerStartParameters(), cancellationToken);
        space.ContainerId = create.ID;
        space.State = WebSpaceState.Running;
        _logger?.Info(LoggerTypes.WebSpaces,
            $"Runtime started {name} image={image} 127.0.0.1:{space.BackendPort}->{containerPort}");
    }

    public async Task StopAsync(WebSpace space, bool kill = false, CancellationToken cancellationToken = default)
    {
        if (!NeedsContainer(space.Runtime))
        {
            space.State = WebSpaceState.Stopped;
            return;
        }

        space.State = WebSpaceState.Stopping;
        ReleaseStdin(space.Uuid);
        var name = RuntimeName(space.Uuid);
        using var client = DockerClientFactory.Create(_docker);

        try
        {
            if (kill)
            {
                await client.Containers.KillContainerAsync(name, new ContainerKillParameters(), cancellationToken);
            }
            else
            {
                await client.Containers.StopContainerAsync(name, new ContainerStopParameters
                {
                    WaitBeforeKillSeconds = 30,
                }, cancellationToken);
            }
        }
        catch (DockerContainerNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        space.State = WebSpaceState.Stopped;
        _logger?.Info(LoggerTypes.WebSpaces, $"Runtime {(kill ? "killed" : "stopped")} {name}");
    }

    public async Task RestartAsync(
        WebSpace space,
        string dataPath,
        string? startup = null,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(space, kill: false, cancellationToken);
        await StartAsync(space, dataPath, startup, cancellationToken);
    }

    public async Task RemoveAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        ReleaseStdin(uuid);
        var name = RuntimeName(uuid);
        try
        {
            using var client = DockerClientFactory.Create(_docker);
            await client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters
            {
                Force = true,
                RemoveVolumes = true,
            }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"Runtime remove {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Write a console command to the container stdin (appends newline).
    /// Throws <see cref="InvalidOperationException"/> when stdin is unavailable.
    /// </summary>
    public async Task SendStdinAsync(Guid uuid, string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        var session = await EnsureStdinAsync(uuid, cancellationToken);
        var bytes = Encoding.UTF8.GetBytes(line.EndsWith('\n') ? line : line + "\n");
        await session.Stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
    }

    /// <summary>
    /// Run a one-shot command inside the runtime container (<c>docker exec</c>).
    /// Used by scheduled tasks (WordPress / WHMCS cron, etc.).
    /// </summary>
    /// <returns>Combined stdout/stderr and the process exit code.</returns>
    public async Task<(long ExitCode, string Output)> ExecCommandAsync(
        Guid uuid,
        string runtime,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var name = RuntimeName(uuid);
        var workDir = MountTarget(runtime);
        using var client = DockerClientFactory.Create(_docker);

        try
        {
            var info = await client.Containers.InspectContainerAsync(name, cancellationToken);
            if (info.State?.Running != true)
                throw new InvalidOperationException("Runtime container is not running.");
        }
        catch (DockerContainerNotFoundException)
        {
            throw new InvalidOperationException("Runtime container not found.");
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Runtime container not found.");
        }

        var created = await client.Exec.ExecCreateContainerAsync(
            name,
            new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Tty = false,
                WorkingDir = workDir,
                Cmd = ["/bin/sh", "-c", command],
            },
            cancellationToken);

        using var stream = await client.Exec.StartAndAttachContainerExecAsync(
            created.ID, tty: false, cancellationToken);

        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        await stream.CopyOutputToAsync(null, stdout, stderr, cancellationToken);

        var output = Encoding.UTF8.GetString(stdout.ToArray());
        var err = Encoding.UTF8.GetString(stderr.ToArray());
        if (err.Length > 0)
            output = string.IsNullOrEmpty(output) ? err : output + "\n" + err;

        var inspect = await client.Exec.InspectContainerExecAsync(created.ID, cancellationToken);
        return (inspect.ExitCode, output.TrimEnd());
    }

    public async Task<string?> InspectStateAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = DockerClientFactory.Create(_docker);
            var info = await client.Containers.InspectContainerAsync(RuntimeName(uuid), cancellationToken);
            if (info.State?.Running == true)
                return WebSpaceState.Running;
            return WebSpaceState.Stopped;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetch the last N lines of container stdout/stderr (combined).</summary>
    public async Task<string> GetLogsAsync(Guid uuid, int lines = 100, CancellationToken cancellationToken = default)
    {
        lines = Math.Clamp(lines, 1, 5000);
        using var client = DockerClientFactory.Create(_docker);
        try
        {
            var stream = await client.Containers.GetContainerLogsAsync(
                RuntimeName(uuid),
                tty: false,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = lines.ToString(),
                    Timestamps = false,
                },
                cancellationToken);

            using var multiplexed = stream;
            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            await multiplexed.CopyOutputToAsync(null, stdout, stderr, cancellationToken);
            var text = Encoding.UTF8.GetString(stdout.ToArray());
            var err = Encoding.UTF8.GetString(stderr.ToArray());
            if (err.Length > 0)
                text = string.IsNullOrEmpty(text) ? err : text + "\n" + err;
            return text;
        }
        catch (DockerContainerNotFoundException)
        {
            throw new InvalidOperationException("Runtime container not found.");
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Runtime container not found.");
        }
    }

    /// <summary>Follow container logs; yields decoded lines until cancelled.</summary>
    public async IAsyncEnumerable<string> FollowLogsAsync(
        Guid uuid,
        int sinceLines = 100,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = DockerClientFactory.Create(_docker);
        MultiplexedStream stream;
        try
        {
            stream = await client.Containers.GetContainerLogsAsync(
                RuntimeName(uuid),
                tty: false,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true,
                    Tail = Math.Clamp(sinceLines, 0, 5000).ToString(),
                    Timestamps = false,
                },
                cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            throw new InvalidOperationException("Runtime container not found.");
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Runtime container not found.");
        }

        using (stream)
        {
            var buffer = new byte[8192];
            var leftover = "";
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                if (result.EOF)
                    yield break;
                if (result.Count <= 0)
                    continue;

                leftover += Encoding.UTF8.GetString(buffer, 0, result.Count);
                var parts = leftover.Split('\n');
                leftover = parts[^1];
                for (var i = 0; i < parts.Length - 1; i++)
                    yield return parts[i];
            }
        }
    }

    public void Dispose()
    {
        foreach (var uuid in _stdin.Keys.ToList())
            ReleaseStdin(uuid);
    }

    private async Task<StdinSession> EnsureStdinAsync(Guid uuid, CancellationToken cancellationToken)
    {
        if (_stdin.TryGetValue(uuid, out var existing))
            return existing;

        var client = DockerClientFactory.Create(_docker);
        try
        {
            var info = await client.Containers.InspectContainerAsync(RuntimeName(uuid), cancellationToken);
            if (info.State?.Running != true)
            {
                client.Dispose();
                throw new InvalidOperationException("Runtime container is not running.");
            }

            if (info.Config?.OpenStdin != true)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    "Runtime container has no stdin; restart the WebSpace to enable console input.");
            }

            var stream = await client.Containers.AttachContainerAsync(
                RuntimeName(uuid),
                tty: false,
                new ContainerAttachParameters
                {
                    Stream = true,
                    Stdin = true,
                    Stdout = false,
                    Stderr = false,
                },
                cancellationToken);

            var session = new StdinSession(client, stream);
            if (!_stdin.TryAdd(uuid, session))
            {
                session.Dispose();
                return _stdin[uuid];
            }

            return session;
        }
        catch (DockerContainerNotFoundException)
        {
            client.Dispose();
            throw new InvalidOperationException("Runtime container not found.");
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            client.Dispose();
            throw new InvalidOperationException("Runtime container not found.");
        }
        catch
        {
            if (!_stdin.ContainsKey(uuid))
                client.Dispose();
            throw;
        }
    }

    private void ReleaseStdin(Guid uuid)
    {
        if (_stdin.TryRemove(uuid, out var session))
            session.Dispose();
    }

    private LogConfig BuildLogConfig()
    {
        var cfg = _docker.LogConfig ?? new DockerLogConfig();
        return new LogConfig
        {
            Type = string.IsNullOrWhiteSpace(cfg.Type) ? "local" : cfg.Type,
            Config = cfg.Config is { Count: > 0 }
                ? new Dictionary<string, string>(cfg.Config)
                : new Dictionary<string, string>
                {
                    ["compress"] = "false",
                    ["max-file"] = "1",
                    ["max-size"] = "5m",
                    ["mode"] = "non-blocking",
                },
        };
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

    private static async Task<ContainerInspectResponse?> TryInspectAsync(
        DockerClient client, string name, CancellationToken ct)
    {
        try
        {
            return await client.Containers.InspectContainerAsync(name, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private sealed class StdinSession : IDisposable
    {
        public StdinSession(DockerClient client, MultiplexedStream stream)
        {
            Client = client;
            Stream = stream;
        }

        public DockerClient Client { get; }
        public MultiplexedStream Stream { get; }

        public void Dispose()
        {
            try { Stream.CloseWrite(); } catch { /* ignore */ }
            try { Stream.Dispose(); } catch { /* ignore */ }
            try { Client.Dispose(); } catch { /* ignore */ }
        }
    }
}
