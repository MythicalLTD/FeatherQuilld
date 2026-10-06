using System.Reflection;
using System.Runtime.Loader;
using FeatherQuilld.Plugins.Abstractions;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Plugins.Host;
using FeatherQuilld.Plugins.Metadata;
using FeatherQuilld.Plugins.Routing;
using FeatherQuilld.Utils.Plugins.Events;
using FeatherQuilld.Utils.Plugins.Host;
using FeatherQuilld.Utils.Plugins.Routing;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ConfigModel = FeatherQuilld.Utils.Config.Config;
using HostLogger = FeatherQuilld.Utils.Logger.Logger;
using LoggerTypes = FeatherQuilld.Utils.Logger.LoggerTypes;
using PluginContext = FeatherQuilld.Plugins.Context.PluginContext;
using PluginMetadata = FeatherQuilld.Plugins.Metadata.PluginMetadata;

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
    private IServiceProvider? _services;

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

                var (plugin, loadContext) = LoadFromAssembly(candidate.AssemblyPath);
                if (plugin is null)
                {
                    reporter?.Detail($"skipped {candidate.FolderName} (no IPlugin)");
                    loadContext?.Unload();
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

                    loadContext?.Unload();

                    if (pluginsConfig.Strict)
                        throw new InvalidOperationException($"Plugin verification failed: {meta.Id}");

                    result.Status = BootStepStatus.Warning;
                    continue;
                }

                if (pluginsConfig.Disabled.Contains(meta.Id, StringComparer.OrdinalIgnoreCase))
                {
                    reporter?.Detail($"skipped {meta.Id} (disabled in config)");
                    loadContext?.Unload();
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
                    LoadContext = loadContext,
                    EffectiveMetadata = meta,
                });
                reporter?.Detail($"loaded {meta.Id} v{meta.Version}");
            }
            catch (Exception ex)
            {
                reporter?.Detail($"load failed: {candidate.FolderName}");
                _logger.Error(LoggerTypes.PluginLoader, $"Failed loading {candidate.AssemblyPath}", ex);
                result.Status = BootStepStatus.Warning;
                if (pluginsConfig.Strict)
                    throw;
            }
        }

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

        foreach (var loaded in _plugins.ToList())
        {
            var meta = loaded.EffectiveMetadata ?? loaded.Instance.Metadata;
            var pluginLogger = new PluginLogger(_logger, meta.Id);
            var settings = GetPluginSettings(meta.Id, meta, loaded.Manifest);
            var ownedEvents = new OwningEventBus(EventBus);
            loaded.OwnedEvents = ownedEvents;

            var configFacade = new PluginConfigFacade(meta.Id, settings, meta.Capabilities);
            var host = BuildHost(configFacade);

            var context = new PluginContext
            {
                Metadata = meta,
                Services = services,
                Events = ownedEvents,
                Routes = RouteRegistry,
                Logger = pluginLogger,
                Host = host,
                Settings = settings,
            };

            loaded.Context = context;

            RouteRegistry.BeginPlugin(meta.Id, meta.Capabilities, _config.Plugins.Strict);
            try
            {
                loaded.Instance.Configure(context);
                reporter?.Detail($"configured {meta.Id}");
                _logger.Debug(LoggerTypes.Plugin, $"Configured '{meta.Id}'");
            }
            catch (PluginCapabilityException ex)
            {
                reporter?.Detail($"configure capability denied: {meta.Id} ({ex.Capability})");
                _logger.Warning(LoggerTypes.Plugin, ex.Message);
                ownedEvents.Dispose();
                RouteRegistry.DisablePlugin(meta.Id);
                _plugins.Remove(loaded);
                result.Status = BootStepStatus.Warning;
                if (_config.Plugins.Strict)
                    throw;
            }
            catch (Exception ex)
            {
                reporter?.Detail($"configure failed: {meta.Id}");
                _logger.Error(LoggerTypes.Plugin, $"Configure failed for '{meta.Id}'", ex);
                ownedEvents.Dispose();
                result.Status = BootStepStatus.Warning;

                if (_config.Plugins.Strict)
                    throw;
            }
            finally
            {
                RouteRegistry.EndPlugin();
            }
        }

        RouteRegistry.ApplyAlterations();

        foreach (var loaded in _plugins)
        {
            EventBus.Emit(new PluginConfiguredEvent
            {
                PluginId = loaded.EffectiveMetadata.Id,
                PluginName = loaded.EffectiveMetadata.Name,
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
            var endpoint = app.MapMethods(route.Pattern, [route.Method], route.Handler)
                .RequireAuthorization();
            if (!string.IsNullOrEmpty(route.Name))
                endpoint.WithName(route.Name);
            if (route.Tags.Length > 0)
                endpoint.WithTags(route.Tags);
        }
    }

    public void OnApplicationStarted(IServiceProvider services)
    {
        _services = services;
        EventBus.Emit(new ApplicationStartedEvent { Services = services });

        foreach (var loaded in _plugins)
        {
            if (loaded.Instance is IPluginLifecycle lifecycle)
            {
                try
                {
                    lifecycle.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.Error(LoggerTypes.Plugin,
                        $"StartAsync failed for '{loaded.EffectiveMetadata.Id}'", ex);
                }
            }

            _logger.Info(LoggerTypes.Plugin, $"'{loaded.EffectiveMetadata.Id}' started");
        }
    }

    public async Task OnApplicationStoppingAsync(CancellationToken cancellationToken)
    {
        foreach (var loaded in _plugins.ToList())
        {
            if (loaded.Instance is IPluginLifecycle lifecycle)
            {
                try
                {
                    await lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning(LoggerTypes.Plugin,
                        $"StopAsync failed for '{loaded.EffectiveMetadata.Id}': {ex.Message}");
                }
            }
        }

        await EventBus.EmitAsync(new ApplicationStoppingEvent { CancellationToken = cancellationToken },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Soft-unload a plugin: stop lifecycle, dispose subscriptions, disable routes, unload ALC.
    /// MVC application parts remain until process restart.
    /// </summary>
    public async Task<bool> UnloadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var loaded = _plugins.FirstOrDefault(p =>
            string.Equals(p.EffectiveMetadata.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (loaded is null)
            return false;

        if (loaded.Instance is IPluginLifecycle lifecycle)
        {
            try
            {
                await lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(LoggerTypes.Plugin, $"StopAsync during unload of '{pluginId}': {ex.Message}");
            }
        }

        loaded.OwnedEvents?.Dispose();
        RouteRegistry.DisablePlugin(pluginId);
        _plugins.Remove(loaded);

        try
        {
            loaded.LoadContext?.Unload();
        }
        catch (Exception ex)
        {
            _logger.Warning(LoggerTypes.Plugin, $"ALC unload for '{pluginId}': {ex.Message}");
        }

        _logger.Info(LoggerTypes.Plugin, $"Unloaded plugin '{pluginId}' (MVC parts require process restart)");
        return true;
    }

    private IPluginHost BuildHost(PluginConfigFacade configFacade)
    {
        var daemon = new DaemonInfoFacade(
            configFacade,
            () => StartupBanner.Version,
            () => _config.Uuid.ToString(),
            () => _services?.GetService<DaemonState>()?.UptimeSeconds ?? 0);

        var spaces = new WebSpaceLookupFacade(
            configFacade,
            uuid =>
            {
                var space = _services?.GetService<WebSpaceStore>()?.Get(uuid);
                return space is null
                    ? null
                    : new WebSpaceSummary(space.Uuid, space.Name, space.Runtime, space.Status);
            },
            () =>
            {
                var store = _services?.GetService<WebSpaceStore>();
                if (store is null)
                    return Array.Empty<WebSpaceSummary>();
                return store.List()
                    .Select(s => new WebSpaceSummary(s.Uuid, s.Name, s.Runtime, s.Status))
                    .ToList();
            });

        return new PluginHostFacade(daemon, spaces, configFacade);
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

    private static PluginMetadata MergeMetadata(PluginMetadata fromPlugin, PluginManifest? manifest)
    {
        if (manifest is null)
            return fromPlugin;

        var capabilities = manifest.Capabilities.Count > 0
            ? (IReadOnlyList<string>)manifest.Capabilities
            : fromPlugin.Capabilities;

        return new PluginMetadata
        {
            Id = manifest.Id ?? fromPlugin.Id,
            Name = manifest.Name ?? fromPlugin.Name,
            Version = manifest.Version ?? fromPlugin.Version,
            Description = manifest.Description ?? fromPlugin.Description,
            Author = manifest.Author ?? fromPlugin.Author,
            MinHostVersion = manifest.MinHostVersion ?? fromPlugin.MinHostVersion,
            Capabilities = capabilities,
            Settings = fromPlugin.Settings,
        };
    }

    private static (IPlugin? Plugin, AssemblyLoadContext? Context) LoadFromAssembly(string dllPath)
    {
        var loadContext = new PluginLoadContext(dllPath);
        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        var pluginTypes = assembly.GetExportedTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        if (pluginTypes.Count == 0)
            return (null, loadContext);

        if (pluginTypes.Count > 1)
            throw new InvalidOperationException($"Multiple IPlugin implementations in {dllPath}");

        var plugin = (IPlugin?)Activator.CreateInstance(pluginTypes[0]);
        return (plugin, loadContext);
    }

    private static List<string> Verify(
        PluginMetadata meta,
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

    public IReadOnlyDictionary<string, object?> GetPluginSettings(
        string pluginId,
        PluginMetadata meta,
        PluginManifest? manifest)
    {
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in meta.Settings)
            merged[key] = value;

        if (manifest?.Settings is not null)
        {
            foreach (var (key, value) in manifest.Settings)
                merged[key] = value;
        }

        if (_config.Plugins.Settings.TryGetValue(pluginId, out var hostSettings) && hostSettings is not null)
        {
            foreach (var (key, value) in hostSettings)
                merged[key] = value;
        }

        return merged;
    }

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

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
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
