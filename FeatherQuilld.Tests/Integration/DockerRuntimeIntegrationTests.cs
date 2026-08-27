using Docker.DotNet;
using Docker.DotNet.Models;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Docker;

namespace FeatherQuilld.Tests.Integration;

/// <summary>Docker-gated smoke tests. No-ops (pass) when the Docker socket is unavailable.</summary>
public sealed class DockerRuntimeIntegrationTests
{
    private static bool DockerAvailable()
    {
        try
        {
            if (!File.Exists("/var/run/docker.sock") && !File.Exists("/run/docker.sock"))
                return false;

            using var client = DockerClientFactory.Create(new DockerConfig { Socket = "/var/run/docker.sock" });
            client.Containers.ListContainersAsync(new ContainersListParameters { Limit = 1 })
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task SendStdin_And_FollowLogs_Smoke()
    {
        if (!DockerAvailable())
            return;

        var docker = new DockerConfig
        {
            Socket = "/var/run/docker.sock",
            Network = { NetworkMode = "bridge" },
        };
        var uuid = Guid.NewGuid();
        var name = WebSpaceRuntime.RuntimeName(uuid);
        using var runtime = new WebSpaceRuntime(docker);

        using var client = DockerClientFactory.Create(docker);
        try
        {
            try { await client.Images.InspectImageAsync("alpine:3.20"); }
            catch
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = "alpine", Tag = "3.20" },
                    null,
                    new Progress<JSONMessage>());
            }

            await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = "alpine:3.20",
                OpenStdin = true,
                AttachStdin = true,
                AttachStdout = true,
                AttachStderr = true,
                Tty = false,
                StdinOnce = false,
                Cmd =
                [
                    "sh", "-c",
                    "while IFS= read -r line; do echo \"got:$line\"; done",
                ],
                HostConfig = new HostConfig { NetworkMode = "bridge" },
            });
            await client.Containers.StartContainerAsync(name, new ContainerStartParameters());

            await runtime.SendStdinAsync(uuid, "quilld-smoke");
            await Task.Delay(500);

            var logs = await runtime.GetLogsAsync(uuid, lines: 50);
            Assert.Contains("got:quilld-smoke", logs, StringComparison.Ordinal);
        }
        finally
        {
            await runtime.RemoveAsync(uuid);
        }
    }

    [Fact]
    public async Task InspectState_StartStop_Smoke()
    {
        if (!DockerAvailable())
            return;

        var docker = new DockerConfig
        {
            Socket = "/var/run/docker.sock",
            Network = { NetworkMode = "bridge" },
        };
        var uuid = Guid.NewGuid();
        var name = WebSpaceRuntime.RuntimeName(uuid);
        using var runtime = new WebSpaceRuntime(docker);
        using var client = DockerClientFactory.Create(docker);

        try
        {
            try { await client.Images.InspectImageAsync("alpine:3.20"); }
            catch
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = "alpine", Tag = "3.20" },
                    null,
                    new Progress<JSONMessage>());
            }

            await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = "alpine:3.20",
                OpenStdin = true,
                AttachStdin = true,
                Cmd = ["sleep", "60"],
                HostConfig = new HostConfig { NetworkMode = "bridge" },
            });
            await client.Containers.StartContainerAsync(name, new ContainerStartParameters());

            var state = await runtime.InspectStateAsync(uuid);
            Assert.Equal("running", state);

            // Stop via Remove force
            await runtime.RemoveAsync(uuid);
            Assert.Null(await runtime.InspectStateAsync(uuid));
        }
        finally
        {
            try { await runtime.RemoveAsync(uuid); } catch { /* ignore */ }
        }
    }
}
