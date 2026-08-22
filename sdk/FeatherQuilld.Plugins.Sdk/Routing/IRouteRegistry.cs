using FeatherQuilld.Plugins.Sdk.Events;
using Microsoft.AspNetCore.Http;

namespace FeatherQuilld.Plugins.Sdk.Routing;

public interface IRouteRegistry
{
    RouteBuilder MapGet(string pattern, Delegate handler, string? name = null);
    RouteBuilder MapPost(string pattern, Delegate handler, string? name = null);
    RouteBuilder MapPut(string pattern, Delegate handler, string? name = null);
    RouteBuilder MapDelete(string pattern, Delegate handler, string? name = null);
    RouteBuilder MapPatch(string pattern, Delegate handler, string? name = null);
    void Before(string pattern, Func<HttpContext, HookResult> hook, int priority = 0);
    void After(string pattern, Func<HttpContext, object?, HookResult> hook, int priority = 0);
    void Alter(string pattern, Action<RouteDescriptor> alter);
}

public sealed class RouteBuilder
{
    public required RouteDescriptor Descriptor { get; init; }

    public RouteBuilder WithName(string name)
    {
        Descriptor.Name = name;
        return this;
    }

    public RouteBuilder WithTags(params string[] tags)
    {
        Descriptor.Tags = tags;
        return this;
    }
}

public sealed class RouteDescriptor
{
    public required string Pattern { get; init; }
    public required string Method { get; init; }
    public required Delegate Handler { get; init; }
    public string? Name { get; set; }
    public string[] Tags { get; set; } = [];
    public string? PluginId { get; set; }
}
