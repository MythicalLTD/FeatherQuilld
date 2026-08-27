using FeatherQuilld.Middleware;
using FeatherQuilld.Utils.Plugins;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.Services;
using FeatherQuilld.Utils.WebSpaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
using Prometheus;
using Scalar.AspNetCore;
using System.Threading.RateLimiting;
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

                // Eager-load WebSpaces so FuseQuota remounts / proxy rebuild happen at boot.
                var spaces = app.Services.GetRequiredService<WebSpaceStore>();
                var scheduleManager = app.Services.GetRequiredService<WebSpaceScheduleManager>();
                spaces.BindScheduleManager(scheduleManager);

                return BootStepResult.Merge(loadResult, configureResult);
            })
            .Step("Self-tests", reporter =>
            {
                ArgumentNullException.ThrowIfNull(app);
                ArgumentNullException.ThrowIfNull(logger);

                var spaces = app.Services.GetRequiredService<WebSpaceStore>();
                var diagnostics = app.Services.GetRequiredService<Utils.SystemInfo.DiagnosticsRegistry>();
                return StartupSelfTest.Run(config, spaces, logger, reporter, diagnostics);
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
        Logger.Debug(LoggerTypes.Application,
            $"paths data={config.System.Data} eggs={config.System.EggsDirectory} vmounts={config.System.VmountDirectory}");
        Logger.Info(LoggerTypes.WebServer, $"Listening on {config.Api.Host}:{config.Api.Port}");

        if (config.HasPanelCredentials())
            Logger.Info(LoggerTypes.Application, $"Panel → {config.Remote.Panel}");

        if (config.Api.Docs.Enabled)
            Logger.Info(LoggerTypes.WebServer, "API docs → /scalar (OpenAPI → /openapi/v1.json)");

        if (Plugins.Plugins.Count > 0)
            Logger.Info(LoggerTypes.PluginLoader, $"{Plugins.Plugins.Count} plugin(s) active");

        var spaces = App.Services.GetService<WebSpaceStore>();
        if (spaces is not null)
            Logger.Info(LoggerTypes.WebSpaces, $"{spaces.List().Count} WebSpace(s) loaded");

        if (config.Sftp.Enabled)
            Logger.Info(LoggerTypes.Application, $"SFTP → 0.0.0.0:{config.Sftp.Port}");
        else
            Logger.Info(LoggerTypes.Application, "SFTP disabled");

        Logger.Info(LoggerTypes.Disk,
            $"Disk limiter effective={config.System.EffectiveDiskLimiterMode} (configured={config.System.DiskLimiterMode})");
        Logger.Info(LoggerTypes.Proxy,
            $"Reverse proxy {(config.System.Proxy.Enabled ? "on" : "off")} ({config.System.Proxy.Provider})");

        if (config.Debug)
            Logger.Debug(LoggerTypes.Application, "Debug logging enabled");
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
        builder.Services.AddSingleton<IPanelClient>(sp => sp.GetRequiredService<PanelClient>());
        builder.Services.AddHostedService<PanelSyncService>();
        builder.Services.AddSingleton<ReverseProxyManager>();
        builder.Services.AddSingleton<StaticFileServerManager>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StaticFileServerManager>());
        builder.Services.AddSingleton(sp =>
            new Utils.Docker.PortAllocator(config.System.Proxy));
        builder.Services.AddSingleton(sp =>
            new Utils.Docker.WebSpaceInstaller(config.Docker, sp.GetService<AppLogger>()));
        builder.Services.AddSingleton(sp =>
            new Utils.Docker.WebSpaceRuntime(config.Docker, sp.GetService<AppLogger>()));
        builder.Services.AddSingleton<WebSpaceStore>();
        builder.Services.AddSingleton<Utils.WebSpaces.IWebSpaceFsAccess>(sp =>
            sp.GetRequiredService<WebSpaceStore>());
        builder.Services.AddSingleton<Utils.WebSpaces.Backups.IBackupObjectStore>(sp =>
        {
            var cfg = sp.GetRequiredService<AppConfig>();
            var provider = (cfg.System.Backups?.Provider ?? "local").Trim().ToLowerInvariant();
            return provider switch
            {
                "s3" => new Utils.WebSpaces.Backups.S3BackupStore(cfg.System),
                "restic" => new Utils.WebSpaces.Backups.ResticBackupStore(cfg.System),
                "pbs" => new Utils.WebSpaces.Backups.PbsBackupStore(cfg.System),
                _ => new Utils.WebSpaces.Backups.LocalBackupStore(cfg.System),
            };
        });
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceBackupService>();
        builder.Services.AddSingleton(sp =>
            new Utils.WebSpaces.BackupJobProgressService(sp.GetRequiredService<AppConfig>()));
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceUtilizationService>(sp =>
            new Utils.WebSpaces.WebSpaceUtilizationService(
                sp.GetRequiredService<AppConfig>().Docker,
                sp.GetRequiredService<WebSpaceStore>(),
                sp.GetService<AppLogger>()));
        builder.Services.AddSingleton(sp =>
            new Utils.WebSpaces.TransferProgressService(sp.GetRequiredService<AppConfig>()));
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceTransferService>();
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceFileService>();
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceScheduleManager>();
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceActivityReporter>();
        builder.Services.AddHostedService<Utils.WebSpaces.WebSpaceScheduleHostedService>();
        builder.Services.AddSingleton<Utils.WebSpaces.WebSpaceUserAccessService>();
        builder.Services.AddSingleton<Utils.Auth.ConsoleJwtValidator>();
        builder.Services.AddSingleton<Utils.SystemInfo.HostMetricsSampler>();
        builder.Services.AddSingleton<Utils.SystemInfo.DiagnosticsRegistry>();
        builder.Services.AddHostedService<Utils.Sftp.SftpHostedService>();
        builder.Services.AddSingleton<Utils.Proxy.NginxAcmeService>();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("expensive", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });
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
        app.UseRateLimiter();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseHttpMetrics();
        app.MapMetrics().AllowAnonymous();

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
            try
            {
                pluginManager.OnApplicationStoppingAsync(app.Lifetime.ApplicationStopping)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Soft shutdown — plugins may observe the stopping token.
            }
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
