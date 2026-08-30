using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Logger;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// Optional per-WebSpace Redis sidecar (<c>redis:alpine</c>) + connection metadata under
/// <c>.featherquilld/redis.json</c>. PHP reaches it as hostname <c>redis</c> via Docker link/alias.
/// </summary>
public static class WebSpaceRedisAddon
{
    public const string RelativePath = ".featherquilld/redis.json";
    public const string Image = "redis:alpine";
    public const string HostnameAlias = "redis";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string HostPath(string dataPath) =>
        Path.Combine(dataPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string ContainerName(Guid uuid) => $"quilld-redis-{uuid:N}";

    public static RedisAddonConfig Read(string dataPath)
    {
        var path = HostPath(dataPath);
        if (!File.Exists(path))
            return new RedisAddonConfig();

        try
        {
            var cfg = JsonSerializer.Deserialize<RedisAddonConfig>(File.ReadAllText(path), JsonOptions);
            return cfg ?? new RedisAddonConfig();
        }
        catch
        {
            return new RedisAddonConfig();
        }
    }

    public static void Write(string dataPath, RedisAddonConfig config)
    {
        Directory.CreateDirectory(Path.Combine(dataPath, ".featherquilld"));
        if (string.IsNullOrWhiteSpace(config.Password))
            config.Password = GeneratePassword();
        config.Host = HostnameAlias;
        config.Port = 6379;
        File.WriteAllText(HostPath(dataPath), JsonSerializer.Serialize(config, JsonOptions) + "\n");
    }

    public static RedisAddonConfig Enable(string dataPath)
    {
        var cfg = Read(dataPath);
        cfg.Enabled = true;
        if (string.IsNullOrWhiteSpace(cfg.Password))
            cfg.Password = GeneratePassword();
        Write(dataPath, cfg);

        // Ensure the PHP redis extension is selected.
        var exts = WebSpacePhpExtensions.Read(dataPath);
        if (!exts.Contains("redis", StringComparer.OrdinalIgnoreCase))
        {
            exts.Add("redis");
            WebSpacePhpExtensions.Write(dataPath, exts);
        }

        return cfg;
    }

    public static RedisAddonConfig Disable(string dataPath)
    {
        var cfg = Read(dataPath);
        cfg.Enabled = false;
        Write(dataPath, cfg);
        return cfg;
    }

    public static async Task EnsureRunningAsync(
        DockerClient client,
        DockerConfig docker,
        Guid uuid,
        string dataPath,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = Read(dataPath);
        if (!cfg.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(cfg.Password))
        {
            cfg = Enable(dataPath);
        }

        await DockerNetworkEnsurer.EnsureAsync(docker, logger, cancellationToken).ConfigureAwait(false);

        var name = ContainerName(uuid);
        var networkMode = string.IsNullOrWhiteSpace(docker.Network.NetworkMode)
            ? "bridge"
            : docker.Network.NetworkMode.Trim();

        try
        {
            await client.Images.InspectImageAsync(Image, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerImageNotFoundException)
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "redis", Tag = "alpine" },
                null,
                new Progress<JSONMessage>(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "redis", Tag = "alpine" },
                null,
                new Progress<JSONMessage>(),
                cancellationToken).ConfigureAwait(false);
        }

        var existing = await TryInspectAsync(client, name, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var create = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = Image,
                Cmd = ["redis-server", "--requirepass", cfg.Password],
                HostConfig = new HostConfig
                {
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                    NetworkMode = networkMode,
                },
            }, cancellationToken).ConfigureAwait(false);

            await client.Containers.StartContainerAsync(create.ID, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);
            logger?.Info(LoggerTypes.WebSpaces, $"Redis sidecar started {name}");
            return;
        }

        if (existing.State?.Running != true)
        {
            await client.Containers.StartContainerAsync(name, new ContainerStartParameters(), cancellationToken)
                .ConfigureAwait(false);
            logger?.Info(LoggerTypes.WebSpaces, $"Redis sidecar restarted {name}");
        }
    }

    public static async Task StopAsync(
        DockerClient client,
        Guid uuid,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var name = ContainerName(uuid);
        try
        {
            await client.Containers.StopContainerAsync(name, new ContainerStopParameters
            {
                WaitBeforeKillSeconds = 10,
            }, cancellationToken).ConfigureAwait(false);
            logger?.Info(LoggerTypes.WebSpaces, $"Redis sidecar stopped {name}");
        }
        catch (DockerContainerNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    public static async Task RemoveAsync(
        DockerClient client,
        Guid uuid,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var name = ContainerName(uuid);
        try
        {
            await client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters
            {
                Force = true,
                RemoveVolumes = true,
            }, cancellationToken).ConfigureAwait(false);
            logger?.Info(LoggerTypes.WebSpaces, $"Redis sidecar removed {name}");
        }
        catch (DockerContainerNotFoundException)
        {
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>Docker legacy links so hostname <c>redis</c> resolves from the PHP container on bridge.</summary>
    public static IList<string>? BuildLinks(Guid uuid, string dataPath)
    {
        if (!Read(dataPath).Enabled)
            return null;
        return [$"{ContainerName(uuid)}:{HostnameAlias}"];
    }

    public static IList<string> BuildEnv(string dataPath)
    {
        var cfg = Read(dataPath);
        if (!cfg.Enabled)
            return [];
        return
        [
            $"REDIS_HOST={HostnameAlias}",
            $"REDIS_PORT={cfg.Port}",
            $"REDIS_PASSWORD={cfg.Password}",
        ];
    }

    private static async Task<ContainerInspectResponse?> TryInspectAsync(
        DockerClient client,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.Containers.InspectContainerAsync(name, cancellationToken).ConfigureAwait(false);
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

    private static string GeneratePassword()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', 'x').Replace('/', 'y');
    }
}

public sealed class RedisAddonConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = WebSpaceRedisAddon.HostnameAlias;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 6379;
}
