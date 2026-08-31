namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>Feature flags exposed to the panel for WebSpace file manager UI.</summary>
public static class WebSpaceFileCapabilities
{
    public static IReadOnlyDictionary<string, bool> All { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["trash"] = true,
        ["share"] = true,
        ["wipe_all"] = true,
        ["directory_download"] = true,
        ["archive_browse"] = true,
        ["archive_extract_selection"] = true,
        ["advanced_search"] = true,
        ["abort_install"] = true,
        ["pull_progress"] = true,
        ["signed_upload_url"] = true,
        ["paginated_list"] = true,
        ["compress_7z"] = true,
    };

    public static object ToResponse() =>
        All.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
