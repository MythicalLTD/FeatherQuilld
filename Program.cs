using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Plugins;
using FeatherQuilld.Utils.Startup;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace FeatherQuilld;

public static class Program
{
    public static Logger? Logger { get; private set; }
    public static Config? Config { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var configPath = args
            .SkipWhile(a => a != "--config" && a != "-c")
            .Skip(1)
            .FirstOrDefault()
            ?? Environment.GetEnvironmentVariable("FEATHERQUILLD_CONFIG");

        Config = Config.Load(configPath);

        StartupBanner.Print(Config.AppName, quiet: Config.Quiet);

        WebApplication? app = null;
        WebApplicationBuilder? builder = null;
        PluginManager? pluginManager = null;

        new BootSequence(Config.Quiet)
            .Step("Loading configuration", reporter =>
            {
                reporter.Detail(Path.GetFileName(Config.FilePath));
                return new BootStepResult();
            })
            .Step("Preparing host", _ =>
            {
                builder = WebApplication.CreateBuilder(args);

                builder.Services.AddSingleton(Config);
                builder.Services.AddSingleton(Config.Api);
                builder.Services.AddSingleton(Config.System);
                builder.Services.AddSingleton(Config.Plugins);

                ConfigureKestrel(builder, Config);
                ConfigureLogging(builder, Config);

                pluginManager = new PluginManager(Config, Logger!);
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
                ConfigureCors(builder, Config);
                ConfigureForwardedHeaders(builder);

                if (Config.Api.Docs.Enabled)
                    ConfigureOpenApi(builder, Config);

                app = builder.Build();

                return MergeResults(loadResult, configureResult);
            })
            .Step("Configuring HTTP pipeline", _ =>
            {
                ArgumentNullException.ThrowIfNull(app);
                ArgumentNullException.ThrowIfNull(pluginManager);

                app.UseForwardedHeaders();
                app.UseExceptionHandler();

                if (Config.Api.Ssl.Enabled)
                    app.UseHttpsRedirection();

                app.UseCors();
                app.UseAuthorization();

                pluginManager.ConfigurePipeline(app);

                if (Config.Api.Docs.Enabled)
                {
                    app.MapOpenApi();
                    app.MapScalarApiReference(options =>
                    {
                        options
                            .WithTitle($"{Config.AppName} API")
                            .WithOpenApiRoutePattern("/openapi/{documentName}.json");
                    });
                }

                app.MapControllers();

                app.Lifetime.ApplicationStarted.Register(() =>
                {
                    Logger?.Info(LoggerTypes.WebServer, "HTTP pipeline ready");
                    pluginManager.OnApplicationStarted(app.Services);
                });

                app.Lifetime.ApplicationStopping.Register(() =>
                {
                    Logger?.Info(LoggerTypes.Application, "Shutting down…");
                    pluginManager.OnApplicationStoppingAsync(app.Lifetime.ApplicationStopping)
                        .GetAwaiter().GetResult();
                });

                return new BootStepResult();
            })
            .Run(BuildSummary(app, pluginManager));

        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(Logger);

        Logger.Info(LoggerTypes.Application, $"{Config.AppName} starting ({app.Environment.EnvironmentName})");
        Logger.Info(LoggerTypes.Application, $"Config → {Config.FilePath}");
        Logger.Info(LoggerTypes.Application, $"Logs → {Logger.LogsDirectory}");
        Logger.Info(LoggerTypes.WebServer, $"Listening on {Config.Api.Host}:{Config.Api.Port}");
        if (Config.Api.Docs.Enabled)
            Logger.Info(LoggerTypes.WebServer, "API docs → /scalar (OpenAPI → /openapi/v1.json)");

        if (pluginManager?.Plugins.Count > 0)
            Logger.Info(LoggerTypes.PluginLoader, $"{pluginManager.Plugins.Count} plugin(s) active");

        try
        {
            app.Run();
        }
        catch (Exception ex)
        {
            Logger.Error(LoggerTypes.Application, "Fatal error", ex);
            throw;
        }
    }

    private static BootStepResult MergeResults(params BootStepResult[] results)
    {
        var merged = new BootStepResult();
        if (results.Any(r => r.Status == BootStepStatus.Failed))
            merged.Status = BootStepStatus.Failed;
        else if (results.Any(r => r.Status == BootStepStatus.Warning))
            merged.Status = BootStepStatus.Warning;
        else if (results.All(r => r.Status == BootStepStatus.Skipped))
            merged.Status = BootStepStatus.Skipped;

        return merged;
    }

    private static BootSummary? BuildSummary(WebApplication? app, PluginManager? pluginManager)
    {
        if (Config is null)
            return null;

        var scheme = Config.Api.Ssl.Enabled ? "https" : "http";
        return new BootSummary
        {
            AppName = Config.AppName,
            Version = StartupBanner.Version,
            ListenAddress = $"{scheme}://{Config.Api.Host}:{Config.Api.Port}",
            ConfigPath = Config.FilePath,
            PluginCount = pluginManager?.Plugins.Count ?? 0,
            Plugins = pluginManager?.Plugins.Select(p => p.Instance.Metadata.Id).ToList() ?? [],
            DocsEnabled = Config.Api.Docs.Enabled,
        };
    }

    private static void ConfigureCors(WebApplicationBuilder builder, Config config)
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

    private static void ConfigureOpenApi(WebApplicationBuilder builder, Config config)
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

    private static void ConfigureKestrel(WebApplicationBuilder builder, Config config)
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

    private static void ConfigureLogging(WebApplicationBuilder builder, Config config)
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

        Logger = new Logger(loggingOptions);
        builder.Services.AddSingleton(loggingOptions);
        builder.Services.AddSingleton(Logger);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new MicrosoftLoggerProvider(Logger));
    }
}
