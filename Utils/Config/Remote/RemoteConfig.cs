namespace FeatherQuilld.Utils.Config.Remote;

public class RemoteConfig
{
    public const string DefaultConfigPath = "/api/quilld-remote/config";
    public const string DefaultHealthPath = "/api/quilld-remote/health";

    public string Panel { get; set; } = "https://panel.mythical.systems";

    /// <summary>Panel route for runtime config YAML (<c>/api/quilld-remote/config</c>).</summary>
    public string ConfigPath { get; set; } = DefaultConfigPath;

    /// <summary>Panel route for health JSON (<c>/api/quilld-remote/health</c>).</summary>
    public string HealthPath { get; set; } = DefaultHealthPath;

    public string AppName { get; set; } = "FeatherPanel";

    /// <summary>Request timeout in seconds for panel API calls.</summary>
    public int Timeout { get; set; } = 30;

    public int RetryLimit { get; set; } = 10;

    public Dictionary<string, string> CustomHeaders { get; set; } = [];

    public string ConfigUrl => BuildUrl(Panel, ConfigPath);

    public string HealthUrl => BuildUrl(Panel, HealthPath);

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DefaultConfigPath;

        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static string BuildUrl(string panel, string path)
    {
        var baseUrl = panel.TrimEnd('/');
        var route = NormalizePath(path);
        return $"{baseUrl}{route}";
    }
}
