using System.Net;
using System.Net.Sockets;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Docker;

/// <summary>Allocates free loopback TCP ports for WebSpace backends.</summary>
public sealed class PortAllocator
{
    private readonly ProxyConfig _proxy;

    public PortAllocator(ProxyConfig proxy) => _proxy = proxy;

    public int Allocate(IEnumerable<WebSpace> existing, int preferred = 0)
    {
        var used = existing
            .Select(s => s.BackendPort)
            .Where(p => p > 0)
            .ToHashSet();

        if (preferred > 0 && !used.Contains(preferred) && IsFree(preferred))
            return preferred;

        var min = _proxy.BackendPortMin > 0 ? _proxy.BackendPortMin : 20000;
        var max = _proxy.BackendPortMax > min ? _proxy.BackendPortMax : 29999;

        for (var port = min; port <= max; port++)
        {
            if (used.Contains(port))
                continue;
            if (IsFree(port))
                return port;
        }

        throw new InvalidOperationException(
            $"No free backend port in range {min}-{max}.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
