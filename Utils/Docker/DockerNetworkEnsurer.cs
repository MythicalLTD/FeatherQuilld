using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Logger;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Docker;

/// <summary>Ensures the configured Docker user-defined network exists before runtime containers start.</summary>
public static class DockerNetworkEnsurer
{
    private static readonly HashSet<string> BuiltInNetworkModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bridge",
        "host",
        "none",
        "default",
    };

    public static bool ShouldEnsure(DockerConfig config)
    {
        var mode = ResolveNetworkName(config);
        return mode is not null;
    }

    public static async Task EnsureAsync(
        DockerConfig config,
        AppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var name = ResolveNetworkName(config);
        if (name is null)
            return;

        using var client = DockerClientFactory.Create(config);
        await EnsureAsync(client, config, name, logger, cancellationToken).ConfigureAwait(false);
    }

    public static void Ensure(DockerConfig config, AppLogger? logger = null)
    {
        EnsureAsync(config, logger).GetAwaiter().GetResult();
    }

    internal static async Task EnsureAsync(
        DockerClient client,
        DockerConfig config,
        string name,
        AppLogger? logger,
        CancellationToken cancellationToken)
    {
        var networks = await client.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [name] = true },
            }},
            cancellationToken).ConfigureAwait(false);

        if (networks.Any(n => string.Equals(n.Name, name, StringComparison.Ordinal)))
        {
            logger?.Debug(LoggerTypes.Application, $"Docker network '{name}' already exists");
            return;
        }

        var net = config.Network;
        var ipamConfigs = new List<IPAMConfig>();

        var v4 = net.Interfaces.V4;
        if (!string.IsNullOrWhiteSpace(v4.Subnet))
        {
            ipamConfigs.Add(new IPAMConfig
            {
                Subnet = v4.Subnet.Trim(),
                Gateway = string.IsNullOrWhiteSpace(v4.Gateway) ? null : v4.Gateway.Trim(),
            });
        }

        if (net.Ipv6)
        {
            var v6 = net.Interfaces.V6;
            if (!string.IsNullOrWhiteSpace(v6.Subnet))
            {
                ipamConfigs.Add(new IPAMConfig
                {
                    Subnet = v6.Subnet.Trim(),
                    Gateway = string.IsNullOrWhiteSpace(v6.Gateway) ? null : v6.Gateway.Trim(),
                });
            }
        }

        var options = new Dictionary<string, string>();
        if (net.NetworkMtu > 0)
            options["com.docker.network.driver.mtu"] = net.NetworkMtu.ToString();
        options["com.docker.network.bridge.enable_icc"] = net.EnableIcc ? "true" : "false";

        var create = new NetworksCreateParameters
        {
            Name = name,
            Driver = string.IsNullOrWhiteSpace(net.Driver) ? "bridge" : net.Driver.Trim(),
            Internal = net.IsInternal,
            EnableIPv6 = net.Ipv6,
            IPAM = ipamConfigs.Count > 0
                ? new IPAM { Config = ipamConfigs }
                : null,
            Options = options.Count > 0 ? options : null,
        };

        try
        {
            await CreateNetworkAsync(client, create, cancellationToken).ConfigureAwait(false);
            logger?.Info(LoggerTypes.Application,
                $"Created Docker network '{name}' driver={create.Driver} ipv6={net.Ipv6}");
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            logger?.Debug(LoggerTypes.Application, $"Docker network '{name}' already exists (race)");
        }
        catch (DockerApiException ex) when (IsSubnetOverlap(ex))
        {
            logger?.Warning(LoggerTypes.Application,
                $"Docker network '{name}' subnet {v4.Subnet} overlaps an existing network — creating with auto IPAM");
            create.IPAM = null;
            create.EnableIPv6 = false;
            try
            {
                await CreateNetworkAsync(client, create, cancellationToken).ConfigureAwait(false);
                logger?.Info(LoggerTypes.Application,
                    $"Created Docker network '{name}' driver={create.Driver} (auto IPAM)");
            }
            catch (DockerApiException retryEx) when (retryEx.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                logger?.Debug(LoggerTypes.Application, $"Docker network '{name}' already exists (race)");
            }
        }
    }

    private static async Task CreateNetworkAsync(
        DockerClient client,
        NetworksCreateParameters create,
        CancellationToken cancellationToken) =>
        await client.Networks.CreateNetworkAsync(create, cancellationToken).ConfigureAwait(false);

    private static bool IsSubnetOverlap(DockerApiException ex) =>
        ex.Message.Contains("overlaps", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Pool overlaps", StringComparison.OrdinalIgnoreCase);

    internal static string? ResolveNetworkName(DockerConfig config)
    {
        var mode = (config.Network.NetworkMode ?? "").Trim();
        if (mode.Length == 0)
            return null;

        if (BuiltInNetworkModes.Contains(mode))
            return null;

        if (mode.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
            return null;

        return mode;
    }
}
