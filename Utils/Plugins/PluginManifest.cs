namespace FeatherQuilld.Utils.Plugins;

/// <summary>Optional <c>plugin.yml</c> manifest inside a plugin folder.</summary>
public sealed class PluginManifest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? MinHostVersion { get; set; }

    /// <summary>Entry assembly file name (defaults to the only plugin DLL in the folder).</summary>
    public string? Main { get; set; }

    public bool Enabled { get; set; } = true;
}
