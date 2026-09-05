using FeatherQuilld.Utils.Config.Api;
using FeatherQuilld.Utils.Config.Docker;
using FeatherQuilld.Utils.Config.Remote;
using FeatherQuilld.Utils.Config.Ftp;
using FeatherQuilld.Utils.Config.Sftp;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Plugins;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using IoPath = System.IO.Path;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;

namespace FeatherQuilld.Utils.Config;

public class Config
{
    public const string DefaultFileName = "config.yml";

    /// <summary>
    /// Placeholder baked into older auto-generated configs. Not a joined node.
    /// </summary>
    public const string PlaceholderPanelUrl = "https://panel.mythical.systems";

    [YamlIgnore]
    public string FilePath { get; set; } = DefaultPath();

    public bool Debug { get; set; }
    public bool Quiet { get; set; }
    public string AppName { get; set; } = "FeatherQuilld";
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public string TokenId { get; set; } = GenerateToken(16);
    public string Token { get; set; } = GenerateToken(64);
    public ApiConfig Api { get; set; } = new();
    public SystemConfig System { get; set; } = new();
    public PluginsConfig Plugins { get; set; } = new();
    public RemoteConfig Remote { get; set; } = new();
    public SftpConfig Sftp { get; set; } = new();
    public FtpConfig Ftp { get; set; } = new();
    public DockerConfig Docker { get; set; } = new();

    [YamlIgnore]
    public string BearerToken => $"{TokenId}.{Token}";

    public static string DefaultPath() =>
        IoPath.Combine("/etc/featherquilld", DefaultFileName);

    /// <summary>
    /// Loads config from disk. The system path is never auto-created a missing
    /// <c>/etc/featherquilld/config.yml</c> means the node is not joined yet.
    /// An explicit <c>--config</c> path outside /etc may still be created for local/dev.
    /// </summary>
    public static Config Load(string? filePath = null)
    {
        var path = filePath ?? DefaultPath();
        var explicitPath = filePath is not null;
        var systemDefault = IsSystemDefaultPath(path);

        if (!File.Exists(path))
        {
            if (systemDefault || !explicitPath)
                throw new ConfigNotReadyException(ConfigNotReadyException.Hint);

            var config = new Config { FilePath = path };
            var baseDir = IoPath.GetDirectoryName(IoPath.GetFullPath(path));
            config.ApplyLocalDevPaths(string.IsNullOrEmpty(baseDir)
                ? Directory.GetCurrentDirectory()
                : baseDir);
            config.EnsureDirectories();
            config.Save();
            return config;
        }

        var yaml = File.ReadAllText(path);
        var loaded = DeserializeYaml(yaml) ?? new Config();
        loaded.FilePath = path;
        return loaded;
    }

    public static bool IsSystemDefaultPath(string path) =>
        string.Equals(
            IoPath.GetFullPath(path),
            IoPath.GetFullPath(DefaultPath()),
            StringComparison.Ordinal);

    /// <summary>
    /// True when this config was produced by join-data / OAuth / a real panel,
    /// not a generated stub with the old placeholder panel URL.
    /// </summary>
    public bool IsJoinedToPanel()
    {
        if (!HasPanelCredentials())
            return false;

        var panel = Remote.Panel.Trim().TrimEnd('/');
        if (string.Equals(panel, PlaceholderPanelUrl, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static Config DeserializeYaml(string yaml) =>
        CreateDeserializer().Deserialize<Config>(yaml) ?? new Config();

    public static string SerializeYaml(Config config) =>
        CreateSerializer().Serialize(config);

    /// <summary>
    /// Validates required join/bootstrap fields.
    /// </summary>
    public void ValidateJoin()
    {
        if (Uuid == Guid.Empty)
            throw new InvalidOperationException("Join config is missing uuid.");

        if (string.IsNullOrWhiteSpace(TokenId))
            throw new InvalidOperationException("Join config is missing token_id.");

        if (string.IsNullOrWhiteSpace(Token))
            throw new InvalidOperationException("Join config is missing token.");

        if (string.IsNullOrWhiteSpace(Remote.Panel))
            throw new InvalidOperationException("Join config is missing remote.panel.");
    }

    /// <summary>
    /// Merges a deserialized runtime config. Prefer <see cref="MergeRuntimeYaml"/> when
    /// the panel payload may omit keys (object defaults would otherwise clobber locals).
    /// </summary>
    public void MergeRuntime(Config runtime) =>
        MergeRuntimeYaml(SerializeYaml(runtime));

    /// <summary>
    /// Deep-merges panel runtime YAML into this config. Only keys present in
    /// <paramref name="runtimeYaml"/> overwrite local values; omitted keys
    /// (e.g. <c>api.port</c>, <c>system.disk_limiter_mode</c>) are preserved.
    /// Auth credentials and local remote panel/paths are always kept when set.
    /// 
    /// 
    /// </summary>
    public void MergeRuntimeYaml(string runtimeYaml)
    {
        var uuid = Uuid;
        var tokenId = TokenId;
        var token = Token;
        var filePath = FilePath;
        // Wings FORBIDDEN_PATHS "remote" analog: keep local callback URL/paths.
        var localPanel = Remote.Panel;
        var localConfigPath = Remote.ConfigPath;
        var localHealthPath = Remote.HealthPath;

        var localMap = ParseYamlMap(SerializeYaml(this));
        var runtimeMap = ParseYamlMap(runtimeYaml);
        if (runtimeMap.Count > 0)
            DeepMergeMaps(localMap, runtimeMap);

        var merged = DeserializeYaml(CreateSerializer().Serialize(localMap));
        Debug = merged.Debug;
        Quiet = merged.Quiet;
        AppName = merged.AppName;
        Api = merged.Api;
        System = merged.System;
        Plugins = merged.Plugins;
        Remote = merged.Remote;
        Sftp = merged.Sftp;
        Ftp = merged.Ftp;
        Docker = merged.Docker;

        Uuid = uuid;
        TokenId = tokenId;
        Token = token;
        FilePath = filePath;

        if (!string.IsNullOrWhiteSpace(localPanel))
            Remote.Panel = localPanel;
        if (!string.IsNullOrWhiteSpace(localConfigPath))
            Remote.ConfigPath = localConfigPath;
        if (!string.IsNullOrWhiteSpace(localHealthPath))
            Remote.HealthPath = localHealthPath;
    }

    /// <summary>
    /// Parses YAML into a mutable string-keyed map (nested maps and sequences preserved).
    /// Empty or null documents yield an empty map.
    /// </summary>
    internal static Dictionary<string, object?> ParseYamlMap(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        var raw = CreateMapDeserializer().Deserialize<object>(yaml);
        return ToStringKeyedMap(raw) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Deep-merges <paramref name="source"/> into <paramref name="target"/>. Nested maps
    /// recurse; scalars and sequences are replaced wholesale.
    /// </summary>
    internal static void DeepMergeMaps(
        IDictionary<string, object?> target,
        IDictionary<string, object?> source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is IDictionary<string, object?> sourceMap
                && target.TryGetValue(key, out var existing)
                && existing is IDictionary<string, object?> targetMap)
            {
                DeepMergeMaps(targetMap, sourceMap);
                continue;
            }

            target[key] = sourceValue;
        }
    }

    private static Dictionary<string, object?>? ToStringKeyedMap(object? value)
    {
        if (value is null)
            return null;

        if (value is IDictionary<object, object> objectKeyed)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, nested) in objectKeyed)
                map[Convert.ToString(key) ?? ""] = NormalizeYamlNode(nested);
            return map;
        }

        if (value is IDictionary<string, object> stringKeyed)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, nested) in stringKeyed)
                map[key] = NormalizeYamlNode(nested);
            return map;
        }

        return null;
    }

    private static object? NormalizeYamlNode(object? value)
    {
        if (value is null)
            return null;

        var asMap = ToStringKeyedMap(value);
        if (asMap is not null)
            return asMap;

        if (value is IList<object> list)
        {
            var normalized = new List<object?>(list.Count);
            foreach (var item in list)
                normalized.Add(NormalizeYamlNode(item));
            return normalized;
        }

        return value;
    }

    private static IDeserializer CreateMapDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    /// <summary>
    /// Applies canonical default paths for a fresh local install.
    /// </summary>
    public void ApplyDefaultPaths()
    {
        ApplyLayout(SystemConfig.DefaultRootDirectory, SystemConfig.DefaultLogDirectory, SystemConfig.DefaultPluginsDirectory);
    }

    /// <summary>
    /// Layout under a writable directory (local/dev). Used when <c>--config</c> is outside /etc.
    /// </summary>
    public void ApplyLocalDevPaths(string baseDirectory)
    {
        var root = IoPath.Combine(baseDirectory, "data");
        ApplyLayout(root, IoPath.Combine(baseDirectory, "logs"), IoPath.Combine(root, "plugins"));
        System.TmpDirectory = IoPath.Combine(baseDirectory, "tmp");
    }

    private void ApplyLayout(string root, string logDirectory, string pluginsDirectory)
    {
        System.RootDirectory = root;
        System.Data = IoPath.Combine(root, "volumes");
        System.Websites = IoPath.Combine(root, "websites");
        System.ArchiveDirectory = IoPath.Combine(root, "archives");
        System.BackupDirectory = IoPath.Combine(root, "backups");
        System.EggsDirectory = IoPath.Combine(root, "eggs");
        System.VmountDirectory = IoPath.Combine(root, "vmounts");
        System.LogDirectory = logDirectory;
        Plugins.Directory = pluginsDirectory;
    }

    public void Save()
    {
        var directory = IoPath.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            CreateDirectoryOrThrow(directory);

        var yaml = SerializeYaml(this);
        File.WriteAllText(FilePath, yaml);
    }

    public void EnsureDirectories()
    {
        CreateDirectoryOrThrow(System.RootDirectory);
        CreateDirectoryOrThrow(System.LogDirectory);
        CreateDirectoryOrThrow(System.Data);
        CreateDirectoryOrThrow(System.Websites);
        CreateDirectoryOrThrow(System.ArchiveDirectory);
        CreateDirectoryOrThrow(System.BackupDirectory);
        CreateDirectoryOrThrow(System.EggsDirectory);
        CreateDirectoryOrThrow(System.VmountDirectory);
        CreateDirectoryOrThrow(IoPath.Combine(System.RootDirectory, "proxy"));
        CreateDirectoryOrThrow(System.TmpDirectory);
        CreateDirectoryOrThrow(Plugins.Directory);
    }

    public bool HasPanelCredentials() =>
        !string.IsNullOrWhiteSpace(Remote.Panel)
        && !string.IsNullOrWhiteSpace(TokenId)
        && !string.IsNullOrWhiteSpace(Token);

    internal static bool CanWriteSystemLayout()
    {
        var configDir = IoPath.GetDirectoryName(DefaultPath());
        return CanCreateDirectory(configDir)
               && CanCreateDirectory(SystemConfig.DefaultRootDirectory)
               && CanCreateDirectory(SystemConfig.DefaultLogDirectory);
    }

    private static bool CanCreateDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (Directory.Exists(path))
            {
                var probe = IoPath.Combine(path, $".featherquilld-write-{Guid.NewGuid():N}");
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return true;
            }

            Directory.CreateDirectory(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CreateDirectoryOrThrow(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new ConfigNotReadyException(
                $"Cannot create '{path}'. {ex.Message}\n\n{ConfigNotReadyException.Hint}",
                ex);
        }
    }

    internal static ISerializer CreateSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();

    internal static IDeserializer CreateDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static string GenerateToken(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        // Use a cryptographically secure RNG: this generates the bearer token
        // that grants full API access to the daemon, so it must not rely on
        // Random.Shared (a non-cryptographic PRNG).
        var randomBytes = new byte[length];
        RandomNumberGenerator.Fill(randomBytes);
        return string.Create(length, (alphabet, randomBytes), static (span, state) =>
        {
            var (chars, bytes) = state;
            for (var i = 0; i < span.Length; i++)
                span[i] = chars[bytes[i] % chars.Length];
        });
    }
}
