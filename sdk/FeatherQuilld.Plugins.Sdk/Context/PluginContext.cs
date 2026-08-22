using FeatherQuilld.Plugins.Sdk.Events;
using FeatherQuilld.Plugins.Sdk.Metadata;
using FeatherQuilld.Plugins.Sdk.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FeatherQuilld.Plugins.Sdk.Context;

/// <summary>Host-provided context passed to <see cref="Abstractions.IPlugin.Configure"/>.</summary>
public sealed class PluginContext
{
    public required PluginMetadata Metadata { get; init; }
    public required IServiceCollection Services { get; init; }
    public required IEventBus Events { get; init; }
    public required IRouteRegistry Routes { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>Plugin-specific settings from config (may be empty).</summary>
    public IReadOnlyDictionary<string, object?> Settings { get; init; } =
        new Dictionary<string, object?>();
}
