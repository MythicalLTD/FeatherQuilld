using System.Net;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

internal static class BackendHostResolver
{
    public static string ResolveUpstream(ProxyConfig proxy, WebSpace? space = null)
    {
        if (!string.IsNullOrWhiteSpace(space?.BackendHost))
            return space.BackendHost.Trim();

        var configured = proxy.BackendHost?.Trim();
        return string.IsNullOrWhiteSpace(configured) ? "127.0.0.1" : configured;
    }

    public static string ResolveBindHost(ProxyConfig proxy) =>
        string.IsNullOrWhiteSpace(proxy.BackendBindHost?.Trim()) ? "127.0.0.1" : proxy.BackendBindHost.Trim();

    public static IPAddress ResolveBindAddress(ProxyConfig proxy)
    {
        var host = ResolveBindHost(proxy);
        if (host is "0.0.0.0" or "*" or "::")
            return IPAddress.Any;

        return IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback;
    }
}
