using Docker.DotNet;
using FeatherQuilld.Utils.Config.Docker;

namespace FeatherQuilld.Utils.Docker;

/// <summary>Creates Docker clients against the host socket.</summary>
public static class DockerClientFactory
{
    public const string DefaultSocket = "/var/run/docker.sock";

    public static DockerClient Create(DockerConfig config)
    {
        var socket = string.IsNullOrWhiteSpace(config.Socket)
            ? DefaultSocket
            : config.Socket.Trim();

        var uri = socket.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(socket)
            : new Uri("unix://" + socket);

        return new DockerClientConfiguration(uri).CreateClient();
    }
}
