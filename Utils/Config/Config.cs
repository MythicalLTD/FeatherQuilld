using FeatherQuilld.Utils.Config.Api;
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
    public string FilePath { get; private set; } = DefaultPath();

    public bool Debug { get; set; }
    public bool Quiet { get; set; }
    public string AppName { get; set; } = "FeatherQuilld";
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public string TokenId { get; set; } = GenerateToken(16);
    public string Token { get; set; } = GenerateToken(64);
    public ApiConfig Api { get; set; } = new();
    public SystemConfig System { get; set; } = new();
    public PluginsConfig Plugins { get; set; } = new();

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
        var loaded = CreateDeserializer().Deserialize<Config>(yaml) ?? new Config();
        loaded.FilePath = path;
        return loaded;
    }

    public void Save()
    {
        var directory = IoPath.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var yaml = CreateSerializer().Serialize(this);
        File.WriteAllText(FilePath, yaml);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(System.RootDirectory);
        Directory.CreateDirectory(System.LogDirectory);
        Directory.CreateDirectory(System.Data);
        Directory.CreateDirectory(System.ArchiveDirectory);
        Directory.CreateDirectory(System.BackupDirectory);
        Directory.CreateDirectory(System.TmpDirectory);

        var directory = Path.IsPathRooted(Plugins.Directory)
            ? Plugins.Directory
            : IoPath.Combine(System.RootDirectory, Plugins.Directory);
        Directory.CreateDirectory(directory);
    }

    private static Config CreateLocalFallback()
    {
        var root = IoPath.GetFullPath("featherquilld-data");
        var config = new Config
        {
            FilePath = IoPath.Combine(Directory.GetCurrentDirectory(), DefaultFileName),
            System =
            {
                RootDirectory = root,
                LogDirectory = IoPath.Combine(root, "logs"),
                Data = IoPath.Combine(root, "volumes"),
                ArchiveDirectory = IoPath.Combine(root, "archives"),
                BackupDirectory = IoPath.Combine(root, "backups"),
                TmpDirectory = IoPath.Combine(root, "tmp"),
                User =
                {
                    Rootless = { Enabled = true },
                    PasswdFile = IoPath.Combine(root, "passwd"),
                },
            },
        };

        config.EnsureDirectories();
        config.Save();
        return config;
    }

    private static ISerializer CreateSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();

    private static IDeserializer CreateDeserializer() =>
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
