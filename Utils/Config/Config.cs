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

namespace FeatherQuilld.Utils.Config;

public class Config
{
    public const string DefaultFileName = "config.yml";

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
    /// Loads config from disk. Creates and saves defaults when the file is missing.
    /// Falls back to <c>./config.yml</c> if the system path is not writable.
    /// </summary>
    public static Config Load(string? filePath = null)
    {
        var path = filePath ?? DefaultPath();
        var explicitPath = filePath is not null;

        if (!File.Exists(path))
        {
            var config = new Config { FilePath = path };
            config.ApplyDefaultPaths();
            try
            {
                config.EnsureDirectories();
                config.Save();
                return config;
            }
            catch (UnauthorizedAccessException) when (!explicitPath)
            {
                return CreateLocalFallback();
            }
            catch (IOException) when (!explicitPath)
            {
                return CreateLocalFallback();
            }
        }

        var yaml = File.ReadAllText(path);
        var loaded = DeserializeYaml(yaml) ?? new Config();
        loaded.FilePath = path;
        return loaded;
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
    /// Merges runtime config from the panel. Runtime values win except auth credentials.
    /// </summary>
    public void MergeRuntime(Config runtime)
    {
        var uuid = Uuid;
        var tokenId = TokenId;
        var token = Token;
        var filePath = FilePath;
        // Wings FORBIDDEN_PATHS "remote" analog: keep local callback URL/paths.
        var localPanel = Remote.Panel;
        var localConfigPath = Remote.ConfigPath;
        var localHealthPath = Remote.HealthPath;

        Debug = runtime.Debug;
        Quiet = runtime.Quiet;
        AppName = runtime.AppName;
        Api = runtime.Api;
        System = runtime.System;
        Plugins = runtime.Plugins;
        Remote = runtime.Remote;
        Sftp = runtime.Sftp;
        Ftp = runtime.Ftp;
        Docker = runtime.Docker;

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
    /// Applies canonical default paths for a fresh local install.
    /// </summary>
    public void ApplyDefaultPaths()
    {
        var root = SystemConfig.DefaultRootDirectory;

        System.RootDirectory = root;
        System.Data = IoPath.Combine(root, "volumes");
        System.Websites = IoPath.Combine(root, "websites");
        System.ArchiveDirectory = IoPath.Combine(root, "archives");
        System.BackupDirectory = IoPath.Combine(root, "backups");
        System.EggsDirectory = IoPath.Combine(root, "eggs");
        System.VmountDirectory = IoPath.Combine(root, "vmounts");
        System.LogDirectory = SystemConfig.DefaultLogDirectory;
        Plugins.Directory = SystemConfig.DefaultPluginsDirectory;
    }

    public void Save()
    {
        var directory = IoPath.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var yaml = SerializeYaml(this);
        File.WriteAllText(FilePath, yaml);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(System.RootDirectory);
        Directory.CreateDirectory(System.LogDirectory);
        Directory.CreateDirectory(System.Data);
        Directory.CreateDirectory(System.Websites);
        Directory.CreateDirectory(System.ArchiveDirectory);
        Directory.CreateDirectory(System.BackupDirectory);
        Directory.CreateDirectory(System.EggsDirectory);
        Directory.CreateDirectory(System.VmountDirectory);
        Directory.CreateDirectory(IoPath.Combine(System.RootDirectory, "proxy"));
        Directory.CreateDirectory(System.TmpDirectory);
        Directory.CreateDirectory(Plugins.Directory);
    }

    public bool HasPanelCredentials() =>
        !string.IsNullOrWhiteSpace(Remote.Panel)
        && !string.IsNullOrWhiteSpace(TokenId)
        && !string.IsNullOrWhiteSpace(Token);

    private static Config CreateLocalFallback()
    {
        var config = new Config
        {
            FilePath = IoPath.Combine(Directory.GetCurrentDirectory(), DefaultFileName),
        };

        config.ApplyDefaultPaths();
        config.EnsureDirectories();
        config.Save();
        return config;
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
        return string.Create(length, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = chars[Random.Shared.Next(chars.Length)];
        });
    }
}
