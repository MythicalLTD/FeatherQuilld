using FeatherQuilld.Middleware;
using FeatherQuilld.Utils.Plugins;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.Startup;

/// <summary>
/// Builds the ASP.NET host, loads plugins, and wires the HTTP pipeline.
/// </summary>
public sealed class DaemonHost
{
    public const string BearerScheme = "Bearer";

    public required WebApplication App { get; init; }
    public required PluginManager Plugins { get; init; }
    public required AppLogger Logger { get; init; }

    public static DaemonHost Build(string[] args, AppConfig config)
    {
        WebApplication? app = null;
        WebApplicationBuilder? builder = null;
        PluginManager? pluginManager = null;
        AppLogger? logger = null;

        new BootSequence(config.Quiet)
            .Step("Loading configuration", reporter =>
            {
                reporter.Detail(Path.GetFileName(config.FilePath));
                return new BootStepResult();
            })
            .Step("Preparing host", _ =>
            {
                builder = WebApplication.CreateBuilder(args);
                RegisterCoreServices(builder, config);
                logger = ConfigureLogging(builder, config);
                ConfigureAuthentication(builder);
                ConfigureKestrel(builder, config);

                pluginManager = new PluginManager(config, logger);
                builder.Services.AddSingleton(pluginManager);
                return new BootStepResult();
            })
            .Step("Loading plugins", reporter =>
            {
                ArgumentNullException.ThrowIfNull(builder);
                ArgumentNullException.ThrowIfNull(pluginManager);

                var loadResult = pluginManager.DiscoverAndLoad(reporter);
                var mvc = builder.Services.AddControllers();
                var configureResult = pluginManager.ConfigureServices(builder.Services, reporter);
                pluginManager.AddControllerParts(mvc);

                builder.Services.AddProblemDetails();
                ConfigureCors(builder, config);
                ConfigureForwardedHeaders(builder);

                if (config.Api.Docs.Enabled)
                    ConfigureOpenApi(builder, config);

                app = builder.Build();
                return BootStepResult.Merge(loadResult, configureResult);
            })
            .Step("Configuring HTTP pipeline", _ =>
            {
                ArgumentNullException.ThrowIfNull(app);
                ArgumentNullException.ThrowIfNull(pluginManager);
                ArgumentNullException.ThrowIfNull(logger);

                ConfigurePipeline(app, config, pluginManager, logger);
                return new BootStepResult();
            })
            .Run(BuildSummary(config, pluginManager));

        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(pluginManager);
        ArgumentNullException.ThrowIfNull(logger);

        return new DaemonHost
        {
            App = app,
            Plugins = pluginManager,
            Logger = logger,
        };
    }

    public void LogStartup(AppConfig config)
    {
        Logger.Info(LoggerTypes.Application, $"{config.AppName} starting ({App.Environment.EnvironmentName})");
        Logger.Info(LoggerTypes.Application, $"Config → {config.FilePath}");
        Logger.Info(LoggerTypes.Application, $"Logs → {Logger.LogsDirectory}");
        Logger.Info(LoggerTypes.WebServer, $"Listening on {config.Api.Host}:{config.Api.Port}");

        if (config.HasPanelCredentials())
            Logger.Info(LoggerTypes.Application, $"Panel → {config.Remote.Panel}");

        if (config.Api.Docs.Enabled)
            Logger.Info(LoggerTypes.WebServer, "API docs → /scalar (OpenAPI → /openapi/v1.json)");

        if (Plugins.Plugins.Count > 0)
            Logger.Info(LoggerTypes.PluginLoader, $"{Plugins.Plugins.Count} plugin(s) active");
    }

    public void Run() => App.Run();

    private static void RegisterCoreServices(WebApplicationBuilder builder, AppConfig config)
    {
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(config.Api);
        builder.Services.AddSingleton(config.System);
        builder.Services.AddSingleton(config.Plugins);
        builder.Services.AddSingleton(config.Remote);
        builder.Services.AddSingleton(config.Sftp);
        builder.Services.AddSingleton(config.Docker);
        builder.Services.AddSingleton<DaemonState>();
        builder.Services.AddSingleton<PanelClient>();
        builder.Services.AddHostedService<PanelSyncService>();
    }

    private static void ConfigureAuthentication(WebApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(BearerScheme)
            .AddScheme<AuthenticationSchemeOptions, BearerTokenAuthenticationHandler>(BearerScheme, null);
        builder.Services.AddAuthorization();
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, AppConfig config)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = config.Api.UploadLimitBytes;
        });

        var url = config.Api.Ssl.Enabled
            ? $"https://{config.Api.Host}:{config.Api.Port}"
            : $"http://{config.Api.Host}:{config.Api.Port}";

        builder.WebHost.UseUrls(url);
    }

    private static AppLogger ConfigureLogging(WebApplicationBuilder builder, AppConfig config)
    {
        var loggingOptions = builder.Configuration
            .GetSection(LoggerOptions.SectionName)
            .Get<LoggerOptions>() ?? new LoggerOptions();

        if (string.IsNullOrWhiteSpace(loggingOptions.Directory) || loggingOptions.Directory == "logs")
            loggingOptions.Directory = config.System.LogDirectory;
        else if (!Path.IsPathRooted(loggingOptions.Directory))
            loggingOptions.Directory = Path.Combine(builder.Environment.ContentRootPath, loggingOptions.Directory);

        if (builder.Environment.IsDevelopment() || config.Debug)
            loggingOptions.Debug = true;

        var logger = new AppLogger(loggingOptions);
        builder.Services.AddSingleton(loggingOptions);
        builder.Services.AddSingleton(logger);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new MicrosoftLoggerProvider(logger));
        return logger;
    }

    private static void ConfigureCors(WebApplicationBuilder builder, AppConfig config)
    {
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var origins = config.Api.AllowedOrigins;
                if (origins.Count == 0)
                {
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    return;
                }

                policy.WithOrigins(origins.ToArray())
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });
    }

    private static void ConfigureForwardedHeaders(WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    private static void ConfigureOpenApi(WebApplicationBuilder builder, AppConfig config)
    {
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = $"{config.AppName} API",
                    Version = "v1",
                    Description = "FeatherQuilld daemon HTTP API.",
                };
                return Task.CompletedTask;
            });
        });
    }

    private static void ConfigurePipeline(
        WebApplication app,
        AppConfig config,
        PluginManager pluginManager,
        AppLogger logger)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();

        if (config.Api.Ssl.Enabled)
            app.UseHttpsRedirection();

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        pluginManager.ConfigurePipeline(app);

        if (config.Api.Docs.Enabled)
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options =>
            {
                options
                    .WithTitle($"{config.AppName} API")
                    .WithOpenApiRoutePattern("/openapi/{documentName}.json");
            });
        }

        app.MapControllers();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            logger.Info(LoggerTypes.WebServer, "HTTP pipeline ready");
            pluginManager.OnApplicationStarted(app.Services);
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            logger.Info(LoggerTypes.Application, "Shutting down…");
            pluginManager.OnApplicationStoppingAsync(app.Lifetime.ApplicationStopping)
                .GetAwaiter().GetResult();
        });
    }

    private static BootSummary? BuildSummary(AppConfig config, PluginManager? pluginManager)
    {
        var scheme = config.Api.Ssl.Enabled ? "https" : "http";
        return new BootSummary
        {
            AppName = config.AppName,
            Version = StartupBanner.Version,
            ListenAddress = $"{scheme}://{config.Api.Host}:{config.Api.Port}",
            ConfigPath = config.FilePath,
            PluginCount = pluginManager?.Plugins.Count ?? 0,
            Plugins = pluginManager?.Plugins.Select(p => p.Instance.Metadata.Id).ToList() ?? [],
            DocsEnabled = config.Api.Docs.Enabled,
        };
    }
}
