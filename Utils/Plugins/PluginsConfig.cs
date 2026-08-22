namespace FeatherQuilld.Utils.Plugins;

public class PluginsConfig
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "/var/lib/featherquilld/plugins";
    public bool Strict { get; set; }

    /// <summary>Plugin IDs to skip even if present on disk.</summary>
    public List<string> Disabled { get; set; } = [];
}
