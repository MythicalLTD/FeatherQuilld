using System.Collections.Concurrent;
using FeatherQuilld.Plugins.Events;
using System.Net;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.WebSpaces;
using FeatherQuilld.Utils.WebSpaces.Disk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Loopback static file servers for Traefik-backed static WebSpaces.
/// Traefik only reverse-proxies; each static space gets a Kestrel listener on its backend_port.
/// </summary>
public sealed class StaticFileServerManager : IHostedService, IDisposable
{
    private readonly AppConfig _config;
    private readonly AppLogger? _logger;
    private readonly IEventBus _events;
    private readonly ConcurrentDictionary<Guid, RunningServer> _servers = new();
    private readonly object _gate = new();
    private bool _disposed;

    public StaticFileServerManager(AppConfig config, AppLogger? logger = null, IEventBus? events = null)
    {
        _config = config;
        _logger = logger;
        _events = events.OrNoOp();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<RunningServer> snapshot;
        lock (_gate)
        {
            snapshot = _servers.Values.ToList();
            _servers.Clear();
        }

        foreach (var server in snapshot)
            await StopServerAsync(server).ConfigureAwait(false);
    }

    /// <summary>
    /// Start/stop/reload loopback file servers to match current Traefik static WebSpaces.
    /// </summary>
    public void Sync(IEnumerable<WebSpace> spaces)
    {
        var snapshot = spaces as IList<WebSpace> ?? spaces.ToList();
        _events.WithHooks(
            new StaticFileSyncBeforeEvent { WebSpaceCount = snapshot.Count },
            err => new StaticFileSyncAfterEvent { WebSpaceCount = snapshot.Count, Error = err },
            () => SyncCore(snapshot));
    }

    private void SyncCore(IEnumerable<WebSpace> spaces)
    {
        if (_disposed)
            return;

        var provider = (_config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant();
        var traefikOn = _config.System.Proxy.Enabled && provider == "traefik";

        var desired = new Dictionary<Guid, (int Port, string Root)>();
        if (traefikOn)
        {
            foreach (var space in spaces)
            {
                if (WebSpaceRuntime.NeedsContainer(space.Runtime))
                    continue;
                if (space.BackendPort <= 0)
                    continue;
                if (space.Status != WebSpaceStatus.Installed && space.Status != WebSpaceStatus.Installing)
                    continue;

                desired[space.Uuid] = (space.BackendPort, ResolveContentRoot(space));
            }
        }

        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var uuid in _servers.Keys.ToList())
            {
                if (!desired.ContainsKey(uuid))
                {
                    if (_servers.TryRemove(uuid, out var stale))
                        _ = StopServerAsync(stale);
                }
            }

            foreach (var (uuid, (port, root)) in desired)
            {
                if (_servers.TryGetValue(uuid, out var existing))
                {
                    if (existing.Port == port &&
                        string.Equals(existing.Root, root, StringComparison.Ordinal) &&
                        Directory.Exists(root))
                    {
                        continue;
                    }

                    if (_servers.TryRemove(uuid, out var old))
                        _ = StopServerAsync(old);
                }

                try
                {
                    Directory.CreateDirectory(root);
                    var started = StartServer(uuid, port, root);
                    _servers[uuid] = started;
                    _logger?.Info(LoggerTypes.Proxy,
                        $"Static file server {uuid} → http://127.0.0.1:{port} root={root}");
                }
                catch (Exception ex)
                {
                    _logger?.Warning(LoggerTypes.Proxy,
                        $"Failed to start static file server for {uuid} on :{port}: {ex.Message}");
                }
            }
        }
    
    }

    private RunningServer StartServer(Guid uuid, int port, string root)
    {
        var cts = new CancellationTokenSource();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        var app = builder.Build();
        var provider = new PhysicalFileProvider(root);
        var contentTypes = new FileExtensionContentTypeProvider();
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = provider,
            RequestPath = "",
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            RequestPath = "",
            ContentTypeProvider = contentTypes,
            ServeUnknownFileTypes = true,
        });
        app.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Not found").ConfigureAwait(false);
        });

        var runTask = app.RunAsync(cts.Token);
        return new RunningServer(uuid, port, root, app, cts, runTask);
    }

    private async Task StopServerAsync(RunningServer server)
    {
        try
        {
            server.Cts.Cancel();
            await server.App.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Debug(LoggerTypes.Proxy, $"Static file server stop {server.Uuid}: {ex.Message}");
        }
        finally
        {
            try { await server.App.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            try { server.Cts.Dispose(); } catch { /* ignore */ }
            _logger?.Debug(LoggerTypes.Proxy, $"Static file server stopped {server.Uuid}");
        }
    }

    private string ResolveContentRoot(WebSpace space)
    {
        var basePath = _config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota
            ? FuseQuotaLimiter.GetMountPath(_config.System, space.Uuid)
            : Path.Combine(_config.System.Data, space.Uuid.ToString());
        return WebSpaceStore.ResolveContentRootPath(basePath, space.DocumentRoot);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed record RunningServer(
        Guid Uuid,
        int Port,
        string Root,
        WebApplication App,
        CancellationTokenSource Cts,
        Task RunTask);
}
