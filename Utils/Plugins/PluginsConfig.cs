namespace FeatherQuilld.Utils.Plugins;

using FeatherQuilld.Utils.Config.System;

public class PluginsConfig
{
    /// <summary>Plugins are off by default; enabling them is an explicit root-equivalent trust decision.</summary>
    public bool Enabled { get; set; }
    public string Directory { get; set; } = SystemConfig.DefaultPluginsDirectory;
    public bool Strict { get; set; }

    /// <summary>Plugin IDs to skip even if present on disk.</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>
    /// Per-plugin settings keyed by plugin id. Host values override <c>plugin.yml</c> settings.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> Settings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
