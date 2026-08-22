namespace FeatherQuilld.Utils.Plugins;

using FeatherQuilld.Utils.Config.System;

public class PluginsConfig
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = SystemConfig.DefaultPluginsDirectory;
    public bool Strict { get; set; }

    /// <summary>Plugin IDs to skip even if present on disk.</summary>
    public List<string> Disabled { get; set; } = [];
}
