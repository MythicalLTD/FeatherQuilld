using System.Reflection;
using System.Runtime.Loader;
using FeatherQuilld.Plugins.Abstractions;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Plugins.Routing;
using FeatherQuilld.Utils.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Routing;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ConfigModel = FeatherQuilld.Utils.Config.Config;
using HostLogger = FeatherQuilld.Utils.Logger.Logger;
using LoggerTypes = FeatherQuilld.Utils.Logger.LoggerTypes;
using PluginContext = FeatherQuilld.Plugins.Context.PluginContext;

namespace FeatherQuilld.Utils.Plugins;

/// <summary>Discovers, verifies, loads, and wires plugins from per-plugin folders.</summary>
public sealed class PluginManager
{
    private static readonly HashSet<string> SkippedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "FeatherQuilld.Plugins",
    };

    private readonly ConfigModel _config;
    private readonly HostLogger _logger;
    private readonly List<LoadedPlugin> _plugins = [];

    public EventBus EventBus { get; } = new();
    public RouteRegistry RouteRegistry { get; } = new();
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public PluginManager(ConfigModel config, HostLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public BootStepResult DiscoverAndLoad(BootReporter? reporter = null)
    {
        var result = new BootStepResult();
        var pluginsConfig = _config.Plugins;

        if (!pluginsConfig.Enabled)
        {
            reporter?.Detail("plugin system disabled");
            _logger.Info(LoggerTypes.PluginLoader, "Plugin system disabled");
            result.Status = BootStepStatus.Skipped;
            return result;
        }

        var root = ResolvePluginDirectory(pluginsConfig.Directory);
        Directory.CreateDirectory(root);
        reporter?.Detail(root);

        var candidates = DiscoverCandidates(root).ToList();
        if (candidates.Count == 0)
        {
            reporter?.Detail("no plugins found");
            _logger.Info(LoggerTypes.PluginLoader, $"No plugins in {root}");
            return result;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostVersion = Version.Parse(StartupBanner.Version);

        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.Manifest?.Enabled == false)
                {
                    reporter?.Detail($"skipped {candidate.FolderName} (manifest disabled)");
                    continue;
                }

                var plugin = LoadFromAssembly(candidate.AssemblyPath);
                if (plugin is null)
                {
                    reporter?.Detail($"skipped {candidate.FolderName} (no IPlugin)");
                    continue;
                }

                var meta = MergeMetadata(plugin.Metadata, candidate.Manifest);
                var errors = Verify(meta, hostVersion, seenIds, pluginsConfig);
                if (errors.Count > 0)
                {
                    foreach (var error in errors)
                        reporter?.Detail($"{meta.Id}: {error}");

                    foreach (var error in errors)
                        _logger.Warning(LoggerTypes.PluginLoader, $"{candidate.AssemblyPath}: {error}");

                    if (pluginsConfig.Strict)
                        throw new InvalidOperationException($"Plugin verification failed: {meta.Id}");

                    result.Status = BootStepStatus.Warning;
                    continue;
                }

                if (pluginsConfig.Disabled.Contains(meta.Id, StringComparer.OrdinalIgnoreCase))
                {
                    reporter?.Detail($"skipped {meta.Id} (disabled in config)");
                    continue;
                }

                seenIds.Add(meta.Id);
                _plugins.Add(new LoadedPlugin
                {
                    Instance = plugin,
                    Assembly = plugin.GetType().Assembly,
                    Directory = candidate.Directory,
                    AssemblyPath = candidate.AssemblyPath,
                    Manifest = candidate.Manifest,
                });

                reporter?.Detail($"{meta.Name} v{meta.Version} ({meta.Id})");
                _logger.Info(LoggerTypes.PluginLoader,
                    $"Loaded plugin '{meta.Name}' v{meta.Version} ({meta.Id}) from {candidate.Directory}");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                reporter?.Detail($"failed {candidate.FolderName}: {ex.Message}");
                _logger.Error(LoggerTypes.PluginLoader, $"Failed to load {candidate.AssemblyPath}", ex);
                result.Status = BootStepStatus.Warning;

                if (pluginsConfig.Strict)
                    throw;
            }
        }

        if (_plugins.Count == 0 && result.Status == BootStepStatus.Success)
            reporter?.Detail("no valid plugins loaded");

        _logger.Info(LoggerTypes.PluginLoader, $"{_plugins.Count} plugin(s) ready");
        return result;
    }

    public BootStepResult ConfigureServices(IServiceCollection services, BootReporter? reporter = null)
    {
        var result = new BootStepResult();

        services.AddSingleton(EventBus);
        services.AddSingleton<IEventBus>(EventBus);
        services.AddSingleton(RouteRegistry);
        services.AddSingleton<IRouteRegistry>(RouteRegistry);
        services.AddSingleton(this);

        foreach (var loaded in _plugins)
        {
            var meta = loaded.Instance.Metadata;
            var pluginLogger = new PluginLogger(_logger, meta.Id);

            var context = new PluginContext
            {
                Metadata = meta,
                Services = services,
                Events = EventBus,
                Routes = RouteRegistry,
                Logger = pluginLogger,
                Settings = GetPluginSettings(meta.Id),
            };

            loaded.Context = context;

            try
            {
                loaded.Instance.Configure(context);
                reporter?.Detail($"configured {meta.Id}");
                _logger.Debug(LoggerTypes.Plugin, $"Configured '{meta.Id}'");
            }
            catch (Exception ex)
            {
                reporter?.Detail($"configure failed: {meta.Id}");
                _logger.Error(LoggerTypes.Plugin, $"Configure failed for '{meta.Id}'", ex);
                result.Status = BootStepStatus.Warning;

                if (_config.Plugins.Strict)
                    throw;
            }
        }

        RouteRegistry.ApplyAlterations();

        foreach (var loaded in _plugins)
        {
            EventBus.Emit(new PluginConfiguredEvent
            {
                PluginId = loaded.Instance.Metadata.Id,
                PluginName = loaded.Instance.Metadata.Name,
            });
        }

        return result;
    }

    public void AddControllerParts(IMvcBuilder mvc)
    {
        foreach (var loaded in _plugins)
            mvc.AddApplicationPart(loaded.Assembly);
    }

    public void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<PluginEventMiddleware>();

        foreach (var route in RouteRegistry.Routes)
        {
            var endpoint = app.MapMethods(route.Pattern, [route.Method], route.Handler);
            if (!string.IsNullOrEmpty(route.Name))
                endpoint.WithName(route.Name);
            if (route.Tags.Length > 0)
                endpoint.WithTags(route.Tags);
        }
    }

    public void OnApplicationStarted(IServiceProvider services)
    {
        EventBus.Emit(new ApplicationStartedEvent { Services = services });

        foreach (var loaded in _plugins)
            _logger.Info(LoggerTypes.Plugin, $"'{loaded.Instance.Metadata.Id}' started");
    }

    public async Task OnApplicationStoppingAsync(CancellationToken cancellationToken)
    {
        await EventBus.EmitAsync(new ApplicationStoppingEvent { CancellationToken = cancellationToken },
            cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<PluginCandidate> DiscoverCandidates(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var manifest = TryLoadManifest(directory);
            var assemblyPath = ResolveAssemblyPath(directory, manifest);
            if (assemblyPath is not null)
            {
                yield return new PluginCandidate(
                    directory,
                    Path.GetFileName(directory),
                    assemblyPath,
                    manifest);
            }
        }

        // Flat fallback: *.dll directly in the plugins root (legacy layout).
        foreach (var dll in Directory.EnumerateFiles(root, "*.dll"))
        {
            if (IsSkippedAssembly(dll))
                continue;

            yield return new PluginCandidate(
                root,
                Path.GetFileNameWithoutExtension(dll),
                dll,
                null);
        }
    }

    private static string? ResolveAssemblyPath(string directory, PluginManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.Main))
        {
            var explicitPath = Path.Combine(directory, manifest.Main);
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var dlls = Directory.EnumerateFiles(directory, "*.dll")
            .Where(d => !IsSkippedAssembly(d))
            .ToList();

        return dlls.Count switch
        {
            0 => null,
            1 => dlls[0],
            _ => dlls.FirstOrDefault(d =>
                     string.Equals(Path.GetFileNameWithoutExtension(d), Path.GetFileName(directory),
                         StringComparison.OrdinalIgnoreCase))
                 ?? dlls[0],
        };
    }

    private static PluginManifest? TryLoadManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "plugin.yml");
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            return new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<PluginManifest>(yaml);
        }
        catch
        {
            return null;
        }
    }

    private static FeatherQuilld.Plugins.Metadata.PluginMetadata MergeMetadata(
        FeatherQuilld.Plugins.Metadata.PluginMetadata fromPlugin,
        PluginManifest? manifest)
    {
        if (manifest is null)
            return fromPlugin;

        return new FeatherQuilld.Plugins.Metadata.PluginMetadata
        {
            Id = manifest.Id ?? fromPlugin.Id,
            Name = manifest.Name ?? fromPlugin.Name,
            Version = manifest.Version ?? fromPlugin.Version,
            Description = manifest.Description ?? fromPlugin.Description,
            Author = manifest.Author ?? fromPlugin.Author,
            MinHostVersion = manifest.MinHostVersion ?? fromPlugin.MinHostVersion,
        };
    }

    private static IPlugin? LoadFromAssembly(string dllPath)
    {
        var loadContext = new PluginLoadContext(dllPath);
        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        var pluginTypes = assembly.GetExportedTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        if (pluginTypes.Count == 0)
            return null;

        if (pluginTypes.Count > 1)
            throw new InvalidOperationException($"Multiple IPlugin implementations in {dllPath}");

        return (IPlugin?)Activator.CreateInstance(pluginTypes[0]);
    }

    private static List<string> Verify(
        FeatherQuilld.Plugins.Metadata.PluginMetadata meta,
        Version hostVersion,
        ISet<string> seenIds,
        PluginsConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(meta.Id))
            errors.Add("missing plugin id");
        else if (seenIds.Contains(meta.Id))
            errors.Add($"duplicate plugin id '{meta.Id}'");

        if (string.IsNullOrWhiteSpace(meta.Name))
            errors.Add("missing plugin name");

        if (string.IsNullOrWhiteSpace(meta.Version))
            errors.Add("missing plugin version");

        if (!string.IsNullOrWhiteSpace(meta.MinHostVersion)
            && Version.TryParse(meta.MinHostVersion, out var minVersion)
            && hostVersion < minVersion)
        {
            errors.Add($"requires host >= {meta.MinHostVersion}, running {hostVersion}");
        }

        return errors;
    }

    private string ResolvePluginDirectory(string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(SystemConfig.DefaultRootDirectory, configured);

    private IReadOnlyDictionary<string, object?> GetPluginSettings(string pluginId) =>
        new Dictionary<string, object?>();

    private static bool IsSkippedAssembly(string dllPath)
    {
        var name = Path.GetFileNameWithoutExtension(dllPath);
        return SkippedAssemblies.Contains(name)
               || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PluginCandidate(
        string Directory,
        string FolderName,
        string AssemblyPath,
        PluginManifest? Manifest);

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "FeatherQuilld.Plugins" or "FeatherQuilld.PluginSdk")
                return typeof(IPlugin).Assembly;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class PluginLogger(HostLogger host, string pluginId) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = $"[{pluginId}] {formatter(state, exception)}";
            switch (logLevel)
            {
                case LogLevel.Trace or LogLevel.Debug:
                    host.Debug(LoggerTypes.Plugin, message);
                    break;
                case LogLevel.Information:
                    host.Info(LoggerTypes.Plugin, message);
                    break;
                case LogLevel.Warning:
                    host.Warning(LoggerTypes.Plugin, message);
                    break;
                default:
                    if (exception is not null)
                        host.Error(LoggerTypes.Plugin, message, exception);
                    else
                        host.Error(LoggerTypes.Plugin, message);
                    break;
            }
        }
    }
}
