using FeatherQuilld.Plugins.Sdk.Context;
using FeatherQuilld.Plugins.Sdk.Metadata;

namespace FeatherQuilld.Plugins.Sdk.Abstractions;

/// <summary>
/// Entry point for a FeatherQuilld plugin. Implement this and deploy to
/// <c>&lt;plugins-dir&gt;/&lt;plugin-id&gt;/</c>.
/// </summary>
public interface IPlugin
{
    PluginMetadata Metadata { get; }

    /// <summary>Called once during host startup.</summary>
    void Configure(PluginContext context);
}
