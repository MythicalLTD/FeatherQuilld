using FeatherQuilld.Plugins.Abstractions;
using FeatherQuilld.Plugins.Metadata;

namespace FeatherQuilld.Utils.Plugins;

public sealed class LoadedPlugin
{
    public required IPlugin Instance { get; init; }
    public required System.Reflection.Assembly Assembly { get; init; }
    public required string Directory { get; init; }
    public required string AssemblyPath { get; init; }
    public PluginManifest? Manifest { get; init; }
    public FeatherQuilld.Plugins.Context.PluginContext? Context { get; set; }

    public string DisplayName => Instance.Metadata.Name;
    public string DisplayVersion => Instance.Metadata.Version;
}
