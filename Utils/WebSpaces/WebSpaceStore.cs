using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Docker;
using FeatherQuilld.Utils.Proxy;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.WebSpaces.Disk;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// WebSpace registry. Create is Wings-style: panel POSTs uuid, daemon pulls config/install from panel.
/// </summary>
public sealed class WebSpaceStore : IWebSpaceFsAccess
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppConfig _config;
    private readonly IPanelClient _panel;
    private readonly ReverseProxyManager _proxy;
    private readonly PortAllocator _ports;
    private readonly WebSpaceInstaller _installer;
    private readonly WebSpaceRuntime _runtime;
    private readonly NginxAcmeService? _acme;
    private readonly StaticFileServerManager? _staticFiles;
    private readonly AppLogger? _logger;
    private readonly IEventBus _events;
    private readonly WebSpaceWsHub? _wsHub;
    private WebSpaceScheduleManager? _schedules;
    private readonly ConcurrentDictionary<Guid, WebSpace> _spaces = new();
    private readonly ConcurrentDictionary<Guid, byte> _installInFlight = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _installTokens = new();
    private readonly object _mutateGate = new();

    public WebSpaceStore(
        AppConfig config,
        IPanelClient panel,
        ReverseProxyManager proxy,
        PortAllocator ports,
        WebSpaceInstaller installer,
        WebSpaceRuntime runtime,
        AppLogger? logger = null,
        NginxAcmeService? acme = null,
        StaticFileServerManager? staticFiles = null,
        IEventBus? events = null,
        WebSpaceWsHub? wsHub = null)
    {
        _config = config;
        _panel = panel;
        _staticFiles = staticFiles;
        _proxy = proxy;
        _ports = ports;
        _installer = installer;
        _runtime = runtime;
        _logger = logger;
        _acme = acme;
        _events = events.OrNoOp();
        _wsHub = wsHub;

        Directory.CreateDirectory(_config.System.Data);
        Directory.CreateDirectory(_config.System.VmountDirectory);

        LoadFromDisk();
        AttachExistingMounts();
        ReconcileRuntimes();
        RebuildProxy();
    }

    /// <summary>Called after DI to wire schedule sync without a circular dependency.</summary>
    public void BindScheduleManager(WebSpaceScheduleManager schedules) => _schedules = schedules;

    private void TrySyncSchedules(Guid uuid, PanelWebSpaceConfig remote)
    {
        if (_schedules is null)
        {
            return;
        }

        _schedules.SyncSchedules(uuid.ToString("D"), WebSpaceScheduleManager.MapSchedules(remote.Schedules));
    }

    private void NotifySchedulesRemoved(Guid uuid) => _schedules?.RemoveSchedules(uuid.ToString("D"));

    public IReadOnlyList<WebSpace> List() =>
        _spaces.Values.OrderBy(s => s.CreatedAt).ToList();

    public WebSpace? Get(Guid uuid) =>
        _spaces.TryGetValue(uuid, out var space) ? space : null;

    public WebSpaceResponse ToResponse(WebSpace space) =>
        new(
            space.Uuid,
            space.Name,
            space.WebPlateId,
            space.Runtime,
            space.DiskLimitBytes,
            (long)GetDiskUsed(space),
            space.Domains,
            space.Ssl,
            space.BackendPort,
            space.ContainerPort,
            space.DocumentRoot,
            space.ContainerImage,
            space.Status,
            space.State,
            space.CreatedAt,
            space.UpdatedAt,
            space.SslMode,
            space.WafEnabled,
            space.WafDenyIps,
            space.WafDenyPaths,
            space.BandwidthLimitBytes,
            space.BandwidthUsedBytes,
            space.IsBandwidthOverQuota(),
            space.DomainRoutes);

    public WebSpaceStatusResponse ToStatus(WebSpace space) =>
        new(space.Uuid, space.Status, space.State, space.BackendPort, space.ContainerId);

    /// <summary>Wings-style create: pull settings (+ optional install script) from the panel.</summary>
    public WebSpace CreateFromPanel(CreateWebSpaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _events.WithHooks(
            new WebSpaceCreateBeforeEvent { WebSpaceUuid = request.Uuid },
            (_, err) => new WebSpaceCreateAfterEvent { WebSpaceUuid = request.Uuid, Error = err },
            () => CreateFromPanelCore(request));
    }

    private WebSpace CreateFromPanelCore(CreateWebSpaceRequest request)
    {
        if (request.Uuid == Guid.Empty)
            throw new ArgumentException("uuid is required.");

        lock (_mutateGate)
        {
            if (_spaces.TryGetValue(request.Uuid, out var existing))
            {
                if (existing.Status == WebSpaceStatus.Installing)
                    return existing;

                throw new InvalidOperationException($"WebSpace {request.Uuid} already exists on this node.");
            }

            _logger?.Info(LoggerTypes.WebSpaces, $"Fetching WebSpace {request.Uuid} from panel…");
            var remote = _panel.FetchWebSpaceAsync(request.Uuid).GetAwaiter().GetResult();
            if (remote.Uuid == Guid.Empty)
                remote.Uuid = request.Uuid;

            var domains = NormalizeDomains(remote.Domains);
            var domainRoutes = NormalizeDomainRoutes(remote.DomainRoutes, domains);
            domains = domainRoutes.Select(r => r.Domain).ToList();
            foreach (var domain in domains)
            {
                if (!WebSpaceValidation.IsValidDomain(domain))
                    throw new ArgumentException($"Invalid domain '{domain}' from panel.");
            }

            EnsureDomainsAvailable(domains, except: null);

            var diskBytes = remote.Build?.DiskSpace > 0
                ? remote.Build.DiskSpace * 1024L * 1024L
                : 0L;
            var cpuLimit = remote.Build?.CpuLimit > 0 ? remote.Build.CpuLimit : 0;
            var memoryLimitMiB = remote.Build?.MemoryLimit > 0 ? remote.Build.MemoryLimit : 0;
            var bandwidthLimitBytes = remote.Build?.BandwidthLimitGb > 0
                ? remote.Build.BandwidthLimitGb * 1024L * 1024L * 1024L
                : 0L;

            var useFuse = _config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota;
            if ((useFuse || _config.System.Quotas.Enabled) && diskBytes <= 0)
                throw new ArgumentException("Panel build.disk_space must be > 0 when quotas/FuseQuota are enabled.");

            if (useFuse && !FuseQuotaLimiter.IsBinaryAvailable(_config.System))
                throw new InvalidOperationException("fusequota binary is not available.");

            var runtime = string.IsNullOrWhiteSpace(remote.Webplate?.Runtime)
                ? "static"
                : remote.Webplate!.Runtime.Trim().ToLowerInvariant();
            var platePort = remote.Webplate?.ContainerPort ?? 0;
            var containerPort = WebSpaceRuntime.DefaultContainerPort(runtime, platePort);

            var now = DateTimeOffset.UtcNow;
            var space = new WebSpace
            {
                Uuid = remote.Uuid,
                Name = string.IsNullOrWhiteSpace(remote.Name) ? remote.Uuid.ToString() : remote.Name.Trim(),
                WebPlateId = remote.Webplate?.Id?.Trim() ?? "",
                Runtime = runtime,
                DiskLimitBytes = diskBytes,
                CpuLimit = cpuLimit,
                MemoryLimitMiB = memoryLimitMiB,
                BandwidthLimitBytes = bandwidthLimitBytes,
                BandwidthPeriodStart = WebSpaceBandwidthMeter.CurrentPeriodStart().ToString("yyyy-MM-dd"),
                Suspended = remote.Suspended,
                Domains = domains,
                DomainRoutes = domainRoutes,
                Ssl = remote.Ssl,
                SslMode = NormalizeSslMode(remote.SslMode),
                AcmeEmail = NormalizeAcmeEmail(remote.AcmeEmail),
                WafEnabled = remote.WafEnabled,
                WafDenyIps = SanitizeDenyIps(remote.WafDenyIps),
                WafDenyPaths = SanitizeDenyPaths(remote.WafDenyPaths),
                BackendPort = remote.BackendPort,
                BackendHost = NormalizeBackendHost(remote.BackendHost),
                ContainerPort = containerPort,
                DocumentRoot = NormalizeDocumentRoot(remote.Meta?.DocumentRoot),
                ContainerImage = string.IsNullOrWhiteSpace(remote.Webplate?.DockerImage)
                    ? null
                    : remote.Webplate!.DockerImage.Trim(),
                Startup = string.IsNullOrWhiteSpace(remote.Webplate?.Startup)
                    ? null
                    : remote.Webplate!.Startup.Trim(),
                Status = request.SkipScripts ? WebSpaceStatus.Installed : WebSpaceStatus.Installing,
                State = runtime == "static" ? WebSpaceState.Running : WebSpaceState.Stopped,
                CreatedAt = now,
                UpdatedAt = now,
            };

            var dataPath = DataPath(space.Uuid);
            Directory.CreateDirectory(dataPath);

            try
            {
                Persist(space);

                if (useFuse)
                {
                    var limiter = new FuseQuotaLimiter(
                        _config, space.Uuid, dataPath, space.DiskLimitBytes, _logger);
                    limiter.Setup();
                    limiter.StartupAsync().GetAwaiter().GetResult();
                }

                var fsPath = EffectiveFsPath(space.Uuid);

                if (!request.SkipScripts)
                {
                    SeedDocumentRoot(space, fsPath);
                    _spaces[space.Uuid] = space;
                    Persist(space);
                    SyncPanelState(space);
                    RebuildProxy();
                    QueueDeferredInstall(space.Uuid, request, remote, fsPath);
                    _logger?.Info(LoggerTypes.WebSpaces,
                        $"Queued install for {space.Uuid} webplate={space.WebPlateId} runtime={space.Runtime}");
                    return space;
                }

                SeedDocumentRoot(space, fsPath);
                space.Status = WebSpaceStatus.Installed;
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
                _panel.ReportWebSpaceInstallAsync(space.Uuid, successful: true).GetAwaiter().GetResult();

                if (request.StartOnCompletion)
                {
                    try
                    {
                        PowerInternal(space, "start");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(LoggerTypes.WebSpaces,
                            $"start_on_completion failed for {space.Uuid}: {ex.Message}");
                    }
                }

                _spaces[space.Uuid] = space;
                Persist(space);
                SyncPanelState(space);
                RebuildProxy();
                TrySyncSchedules(space.Uuid, remote);
                _logger?.Info(LoggerTypes.WebSpaces,
                    $"Created WebSpace {space.Uuid} webplate={space.WebPlateId} runtime={space.Runtime} state={space.State} domains=[{string.Join(", ", domains)}]");
                return space;
            }
            catch
            {
                TryCleanupFailedCreate(space.Uuid, dataPath, useFuse);
                throw;
            }
        }
    }

    private void QueueDeferredInstall(
        Guid uuid,
        CreateWebSpaceRequest request,
        PanelWebSpaceConfig remote,
        string fsPath)
    {
        if (!_installInFlight.TryAdd(uuid, 0))
            return;

        var cts = new CancellationTokenSource();
        _installTokens[uuid] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunDeferredInstallAsync(uuid, request, remote, fsPath, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _installInFlight.TryRemove(uuid, out _);
                if (_installTokens.TryRemove(uuid, out var token))
                    token.Dispose();
            }
        });
    }

    private async Task RunDeferredInstallAsync(
        Guid uuid,
        CreateWebSpaceRequest request,
        PanelWebSpaceConfig remote,
        string fsPath,
        CancellationToken cancellationToken)
    {
        if (_wsHub is not null)
            await _wsHub.SendInstallStartedAsync(uuid).ConfigureAwait(false);

        try
        {
            var install = await _panel.FetchWebSpaceInstallAsync(uuid).ConfigureAwait(false);

            WebSpace space;
            lock (_mutateGate)
            {
                space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
            }

            await _installer.RunAsync(space, fsPath, install, cancellationToken).ConfigureAwait(false);

            lock (_mutateGate)
            {
                space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                space.Status = WebSpaceStatus.Installed;
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
                _spaces[uuid] = space;
            }

            await _panel.ReportWebSpaceInstallAsync(uuid, successful: true).ConfigureAwait(false);
            _logger?.Info(LoggerTypes.WebSpaces,
                $"Install completed for {uuid} image={space.ContainerImage}");

            if (_wsHub is not null)
            {
                await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Installed).ConfigureAwait(false);
                await _wsHub.SendInstallCompletedAsync(uuid).ConfigureAwait(false);
            }

            lock (_mutateGate)
            {
                var current = Get(uuid);
                if (current is null)
                    return;

                if (request.StartOnCompletion)
                {
                    try
                    {
                        PowerInternal(current, "start");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(LoggerTypes.WebSpaces,
                            $"start_on_completion failed for {uuid}: {ex.Message}");
                    }
                }

                SyncPanelState(current);
                RebuildProxy();
                TrySyncSchedules(uuid, remote);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_mutateGate)
            {
                var space = Get(uuid);
                if (space is not null)
                {
                    space.Status = WebSpaceStatus.Failed;
                    space.State = WebSpaceState.Stopped;
                    space.UpdatedAt = DateTimeOffset.UtcNow;
                    Persist(space);
                }
            }

            try
            {
                await _panel.ReportWebSpaceInstallAsync(uuid, successful: false).ConfigureAwait(false);
            }
            catch (Exception reportEx)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to report install abort: {reportEx.Message}");
            }

            if (_wsHub is not null)
            {
                await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Failed).ConfigureAwait(false);
                await _wsHub.SendInstallFailedAsync(uuid, "Install aborted").ConfigureAwait(false);
            }

            _logger?.Info(LoggerTypes.WebSpaces, $"Install aborted for {uuid}");
        }
        catch (Exception ex)
        {
            lock (_mutateGate)
            {
                var space = Get(uuid);
                if (space is not null)
                {
                    space.Status = WebSpaceStatus.Failed;
                    space.State = WebSpaceState.Stopped;
                    space.UpdatedAt = DateTimeOffset.UtcNow;
                    Persist(space);
                }
            }

            try
            {
                await _panel.ReportWebSpaceInstallAsync(uuid, successful: false).ConfigureAwait(false);
            }
            catch (Exception reportEx)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to report install: {reportEx.Message}");
            }

            if (_wsHub is not null)
            {
                await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Failed).ConfigureAwait(false);
                await _wsHub.SendInstallFailedAsync(uuid, ex.Message).ConfigureAwait(false);
            }

            _logger?.Error(LoggerTypes.WebSpaces, $"Install failed for {uuid}: {ex.Message}");
        }
    }

    /// <summary>Cancel an in-flight WebSpace install or reinstall job.</summary>
    public bool AbortInstall(Guid uuid)
    {
        if (!_installInFlight.ContainsKey(uuid))
            return false;

        if (_installTokens.TryGetValue(uuid, out var cts))
            cts.Cancel();

        try
        {
            _installer.CleanupAsync(uuid).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"Install abort cleanup for {uuid}: {ex.Message}");
        }

        return true;
    }

    /// <summary>Pull latest panel config and apply domains, ssl, disk, document_root, proxy.</summary>
    public WebSpace ApplyConfigFromPanel(Guid uuid) =>
        _events.WithHooks(
            new WebSpaceSyncBeforeEvent { WebSpaceUuid = uuid },
            (_, err) => new WebSpaceSyncAfterEvent { WebSpaceUuid = uuid, Error = err },
            () => ApplyConfigFromPanelCore(uuid));

    public WebSpace RecreateRuntime(Guid uuid)
    {
        lock (_mutateGate)
        {
            var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
            if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
                throw new InvalidOperationException("Static WebSpaces do not use a runtime container.");
            if (string.IsNullOrWhiteSpace(space.ContainerImage))
                throw new InvalidOperationException("No container image configured for this WebSpace.");

            _logger?.Info(LoggerTypes.WebSpaces, $"Recreating runtime for {uuid} image={space.ContainerImage}");
            var fsPath = EffectiveFsPath(uuid);
            _runtime.StopAsync(space, kill: false).GetAwaiter().GetResult();
            _runtime.RemoveAsync(uuid).GetAwaiter().GetResult();
            _runtime.StartAsync(space, fsPath, space.Startup).GetAwaiter().GetResult();
            space.UpdatedAt = DateTimeOffset.UtcNow;
            Persist(space);
            SyncPanelState(space);
            RebuildProxy();
            return space;
        }
    }

    private WebSpace ApplyConfigFromPanelCore(Guid uuid)
    {
        lock (_mutateGate)
        {
            var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");

            _logger?.Info(LoggerTypes.WebSpaces, $"Syncing WebSpace {uuid} config from panel…");
            var remote = _panel.FetchWebSpaceAsync(uuid).GetAwaiter().GetResult();
            if (remote.Uuid == Guid.Empty)
                remote.Uuid = uuid;

            var domains = NormalizeDomains(remote.Domains);
            var domainRoutes = NormalizeDomainRoutes(remote.DomainRoutes, domains);
            domains = domainRoutes.Select(r => r.Domain).ToList();
            foreach (var domain in domains)
            {
                if (!WebSpaceValidation.IsValidDomain(domain))
                    throw new ArgumentException($"Invalid domain '{domain}' from panel.");
            }

            EnsureDomainsAvailable(domains, except: uuid);

            var diskBytes = remote.Build?.DiskSpace > 0
                ? remote.Build.DiskSpace * 1024L * 1024L
                : space.DiskLimitBytes;
            // Always apply panel limits (including 0 = unlimited) when Build is present.
            var cpuLimit = remote.Build is not null ? remote.Build.CpuLimit : space.CpuLimit;
            var memoryLimitMiB = remote.Build is not null ? remote.Build.MemoryLimit : space.MemoryLimitMiB;
            var bandwidthLimitBytes = remote.Build?.BandwidthLimitGb > 0
                ? remote.Build.BandwidthLimitGb * 1024L * 1024L * 1024L
                : remote.Build is not null ? 0L : space.BandwidthLimitBytes;

            var runtime = string.IsNullOrWhiteSpace(remote.Webplate?.Runtime)
                ? space.Runtime
                : remote.Webplate!.Runtime.Trim().ToLowerInvariant();
            if (!string.Equals(runtime, space.Runtime, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime family cannot be changed via sync; reinstall required.");

            var platePort = remote.Webplate?.ContainerPort ?? 0;
            var containerPort = platePort > 0
                ? platePort
                : (space.ContainerPort > 0 ? space.ContainerPort : WebSpaceRuntime.DefaultContainerPort(runtime));
            var containerImage = string.IsNullOrWhiteSpace(remote.Webplate?.DockerImage)
                ? space.ContainerImage
                : remote.Webplate!.DockerImage.Trim();
            var startup = string.IsNullOrWhiteSpace(remote.Webplate?.Startup)
                ? space.Startup
                : remote.Webplate!.Startup.Trim();
            var webPlateId = string.IsNullOrWhiteSpace(remote.Webplate?.Id)
                ? space.WebPlateId
                : remote.Webplate!.Id.Trim();

            space.Name = string.IsNullOrWhiteSpace(remote.Name) ? space.Name : remote.Name.Trim();
            space.WebPlateId = webPlateId;
            space.Runtime = runtime;
            space.ContainerImage = containerImage;
            space.ContainerPort = containerPort;
            space.Startup = startup;
            space.Domains = domains;
            space.DomainRoutes = domainRoutes;
            space.Ssl = remote.Ssl;
            space.SslMode = NormalizeSslMode(remote.SslMode);
            space.AcmeEmail = NormalizeAcmeEmail(remote.AcmeEmail);
            space.WafEnabled = remote.WafEnabled;
            space.WafDenyIps = SanitizeDenyIps(remote.WafDenyIps);
            space.WafDenyPaths = SanitizeDenyPaths(remote.WafDenyPaths);
            space.DiskLimitBytes = diskBytes;
            space.CpuLimit = cpuLimit;
            space.MemoryLimitMiB = memoryLimitMiB;
            space.BandwidthLimitBytes = bandwidthLimitBytes;
            if (string.IsNullOrWhiteSpace(space.BandwidthPeriodStart))
                space.BandwidthPeriodStart = WebSpaceBandwidthMeter.CurrentPeriodStart().ToString("yyyy-MM-dd");
            space.DocumentRoot = remote.Meta is null
                ? space.DocumentRoot
                : NormalizeDocumentRoot(remote.Meta.DocumentRoot);

            if (remote.BackendPort > 0 && remote.BackendPort != space.BackendPort)
            {
                var previousPort = space.BackendPort;
                space.BackendPort = remote.BackendPort;
                if (WebSpaceRuntime.NeedsContainer(space.Runtime) && space.State == WebSpaceState.Running)
                {
                    _logger?.Warning(LoggerTypes.WebSpaces,
                        $"Panel backend_port {remote.BackendPort} applied for {uuid} but container may still listen on {previousPort}; recreate runtime if needed");
                }
            }

            space.BackendHost = NormalizeBackendHost(remote.BackendHost);
            space.Suspended = remote.Suspended;

            space.UpdatedAt = DateTimeOffset.UtcNow;

            if (space.Suspended && space.State == WebSpaceState.Running)
            {
                try
                {
                    PowerInternal(space, "stop");
                }
                catch (Exception ex)
                {
                    _logger?.Warning(LoggerTypes.WebSpaces,
                        $"Failed to stop suspended WebSpace {uuid}: {ex.Message}");
                }
            }

            var useFuse = _config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota;
            if (useFuse && diskBytes > 0)
            {
                var dataPath = DataPath(uuid);
                var limiter = new FuseQuotaLimiter(_config, uuid, dataPath, space.DiskLimitBytes, _logger);
                limiter.UpdateDiskLimitAsync((ulong)diskBytes).GetAwaiter().GetResult();
            }

            Persist(space);
            RebuildProxy();
            TrySyncSchedules(uuid, remote);
            _logger?.Info(LoggerTypes.WebSpaces,
                $"Applied panel config for {uuid} domains=[{string.Join(", ", domains)}] ssl={space.Ssl} disk={diskBytes}");
            return space;
        }
    }

    public WebSpace Power(Guid uuid, string action) =>
        _events.WithHooks(
            new WebSpacePowerBeforeEvent { WebSpaceUuid = uuid, Action = action },
            (_, err) => new WebSpacePowerAfterEvent { WebSpaceUuid = uuid, Action = action, Error = err },
            () =>
            {
                lock (_mutateGate)
                {
                    var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                    PowerInternal(space, action);
                    Persist(space);
                    SyncPanelState(space);
                    BroadcastWsStatus(space);
                    RebuildProxy();
                    return space;
                }
            });

    public WebSpaceStatusResponse Status(Guid uuid)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");

        if (WebSpaceRuntime.NeedsContainer(space.Runtime))
        {
            var live = _runtime.InspectStateAsync(uuid).GetAwaiter().GetResult();
            if (live is not null && live != space.State)
            {
                space.State = live;
                Persist(space);
            }
        }

        return ToStatus(space);
    }

    public bool Delete(Guid uuid) =>
        _events.WithHooks(
            new WebSpaceDeleteBeforeEvent { WebSpaceUuid = uuid },
            (deleted, err) => new WebSpaceDeleteAfterEvent
            {
                WebSpaceUuid = uuid,
                Deleted = deleted,
                Error = err,
            },
            () => DeleteCore(uuid));

    private bool DeleteCore(Guid uuid)
    {
        lock (_mutateGate)
        {
            if (!_spaces.TryRemove(uuid, out var space))
                return false;

            var dataPath = DataPath(uuid);

            try { _runtime.RemoveAsync(uuid).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"runtime remove: {ex.Message}"); }

            try { _installer.CleanupAsync(uuid).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"installer cleanup: {ex.Message}"); }

            // Always best-effort destroy leftover FUSE mounts may remain after mode=none.
            try
            {
                var limiter = new FuseQuotaLimiter(
                    _config, uuid, dataPath, space.DiskLimitBytes, _logger);
                limiter.DestroyAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.Disk, $"fusequota destroy {uuid}: {ex.Message}");
            }

            try
            {
                if (Directory.Exists(dataPath))
                    Directory.Delete(dataPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to delete WebSpace data {dataPath}: {ex.Message}");
            }

            RebuildProxy();
            _logger?.Info(LoggerTypes.WebSpaces, $"Deleted WebSpace {uuid}");
            NotifySchedulesRemoved(uuid);
            return true;
        }
    }

    public object GetSslStatus(Guid uuid)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var provider = (_config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant();
        var providerIsTraefik = provider == "traefik";
        var domains = new List<object>();
        foreach (var domain in space.Domains)
        {
            var crt = NginxAcmeService.CertPath(domain);
            var key = NginxAcmeService.KeyPath(domain);
            var caddyPresent = ProbeCaddyCert(domain);
            var notAfter = NginxAcmeService.GetCertNotAfter(domain);
            var daysRemaining = notAfter is null
                ? (int?)null
                : (int)Math.Floor((notAfter.Value - DateTimeOffset.UtcNow).TotalDays);
            domains.Add(new
            {
                domain,
                ssl_enabled = space.Ssl,
                nginx_cert_present = File.Exists(crt) && File.Exists(key),
                caddy_cert_present = caddyPresent,
                traefik_uses_cert_resolver = providerIsTraefik && space.Ssl,
                nginx_cert_path = crt,
                nginx_key_path = key,
                not_after = notAfter,
                days_remaining = daysRemaining,
            });
        }

        return new
        {
            uuid = space.Uuid,
            ssl = space.Ssl,
            ssl_mode = space.SslMode,
            custom_cert_present = CustomSslPaths(space.Uuid).Present,
            custom_cert_not_after = CustomSslPaths(space.Uuid).NotAfter,
            provider = _config.System.Proxy.Provider,
            acme_email = space.ResolveAcmeEmail(_config.System.Proxy.AcmeEmail),
            domains,
        };
    }

    public object GetCustomSslStatus(Guid uuid)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var paths = CustomSslPaths(uuid);
        return new
        {
            uuid,
            ssl_mode = space.SslMode,
            cert_present = paths.CertPresent,
            key_present = paths.KeyPresent,
            not_after = paths.NotAfter,
            cert_path = paths.CertPath,
            key_path = paths.KeyPath,
        };
    }

    public object PutCustomSsl(Guid uuid, Stream certStream, Stream keyStream)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var paths = CustomSslPaths(uuid);
        Directory.CreateDirectory(paths.Directory);
        using (var certOut = File.Create(paths.CertPath))
            certStream.CopyTo(certOut);
        using (var keyOut = File.Create(paths.KeyPath))
            keyStream.CopyTo(keyOut);

        space.SslMode = "custom";
        space.Ssl = true;
        space.UpdatedAt = DateTimeOffset.UtcNow;
        Persist(space);
        RebuildProxy();

        return GetCustomSslStatus(uuid);
    }

    public object DeleteCustomSsl(Guid uuid)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var paths = CustomSslPaths(uuid);
        if (File.Exists(paths.CertPath))
            File.Delete(paths.CertPath);
        if (File.Exists(paths.KeyPath))
            File.Delete(paths.KeyPath);

        if (string.Equals(space.SslMode, "custom", StringComparison.OrdinalIgnoreCase))
            space.SslMode = "acme";
        space.UpdatedAt = DateTimeOffset.UtcNow;
        Persist(space);
        RebuildProxy();

        return GetCustomSslStatus(uuid);
    }

    private readonly record struct CustomSslPathInfo(
        string Directory,
        string CertPath,
        string KeyPath,
        bool CertPresent,
        bool KeyPresent,
        DateTimeOffset? NotAfter)
    {
        public bool Present => CertPresent && KeyPresent;
    }

    private CustomSslPathInfo CustomSslPaths(Guid uuid)
    {
        var dir = Path.Combine(DataPath(uuid), "ssl", "custom");
        var cert = Path.Combine(dir, "cert.pem");
        var key = Path.Combine(dir, "key.pem");
        var certPresent = File.Exists(cert);
        var keyPresent = File.Exists(key);
        DateTimeOffset? notAfter = certPresent ? NginxAcmeService.GetCertNotAfterFromFile(cert) : null;
        return new CustomSslPathInfo(dir, cert, key, certPresent, keyPresent, notAfter);
    }

    /// <summary>Force ACME reissue for this WebSpace's domains, then rebuild the proxy.</summary>
    public Task<object> RenewSslAsync(Guid uuid, CancellationToken cancellationToken = default) =>
        _events.WithHooksAsync(
            new WebSpaceSslRenewBeforeEvent { WebSpaceUuid = uuid },
            (result, err) => new WebSpaceSslRenewAfterEvent
            {
                WebSpaceUuid = uuid,
                Result = result,
                Error = err,
            },
            ct => RenewSslCoreAsync(uuid, ct),
            cancellationToken);

    private async Task<object> RenewSslCoreAsync(Guid uuid, CancellationToken cancellationToken)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!space.Ssl)
            throw new InvalidOperationException("SSL is not enabled for this WebSpace.");
        if (space.Domains.Count == 0)
            throw new InvalidOperationException("No domains configured for SSL renew.");

        var provider = (_config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant();
        var usesDns01 = string.Equals(space.SslMode, "dns01", StringComparison.OrdinalIgnoreCase);
        var usesCustom = string.Equals(space.SslMode, "custom", StringComparison.OrdinalIgnoreCase);

        if (usesCustom)
            throw new InvalidOperationException("Custom SSL certificates cannot be renewed via ACME.");

        if (_acme is not null && (usesDns01 || provider == "nginx"))
        {
            var email = space.ResolveAcmeEmail(_config.System.Proxy.AcmeEmail);
            if (string.IsNullOrWhiteSpace(email))
                throw new InvalidOperationException(
                    "Owner account email is not set and this node has no fallback ACME email.");

            if (usesDns01)
            {
                var apex = ReverseProxyManager.ResolveApexDomain(space);
                if (string.IsNullOrWhiteSpace(apex))
                    throw new InvalidOperationException("No apex domain for wildcard SSL renew.");
                await _acme.EnsureWildcardCertAsync(uuid, apex, email, force: true, cancellationToken);
            }
            else if (provider == "nginx")
            {
                await _acme.EnsureCertsAsync(space.Domains, cancellationToken, force: true, email: email);
            }
        }
        else if (provider == "nginx" && _acme is null)
        {
            throw new InvalidOperationException("ACME service is not available on this node.");
        }

        lock (_mutateGate)
        {
            RebuildProxy();
        }

        return GetSslStatus(uuid);
    }

    public object GetRedis(Guid uuid)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var cfg = WebSpaceRedisAddon.Read(DataPath(uuid));
        return new
        {
            enabled = cfg.Enabled,
            host = cfg.Host,
            port = cfg.Port,
            password = cfg.Password,
        };
    }

    public WebSpace SetRedis(Guid uuid, bool enabled)
    {
        lock (_mutateGate)
        {
            var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
            var dataPath = DataPath(uuid);
            var cfg = enabled ? WebSpaceRedisAddon.Enable(dataPath) : WebSpaceRedisAddon.Disable(dataPath);

            if (WebSpaceRuntime.NeedsContainer(space.Runtime) &&
                string.Equals(space.Runtime, "php", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(space.ContainerImage))
            {
                _runtime.StopAsync(space, kill: false).GetAwaiter().GetResult();
                _runtime.RemoveAsync(uuid).GetAwaiter().GetResult();
                _runtime.StartAsync(space, EffectiveFsPath(uuid), space.Startup).GetAwaiter().GetResult();
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
                SyncPanelState(space);
                RebuildProxy();
            }

            return space;
        }
    }

    private static bool ProbeCaddyCert(string domain)
    {
        try
        {
            // Caddy stores certs under ~/.local/share/caddy or /var/lib/caddy best-effort probe.
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "caddy", "certificates"),
                "/var/lib/caddy/.local/share/caddy/certificates",
                "/var/lib/featherquilld/caddy/certificates",
            };

            foreach (var root in candidates)
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (file.Contains(domain, StringComparison.OrdinalIgnoreCase)
                        && (file.EndsWith(".crt", StringComparison.OrdinalIgnoreCase)
                            || file.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
        }
        catch
        {
            // ignore probe errors
        }

        return false;
    }

    public string DataPath(Guid uuid) =>
        Path.Combine(_config.System.Data, uuid.ToString());

    /// <summary>
    /// Filesystem root for Docker binds / SFTP / backups.
    /// When FuseQuota is active this is the FUSE mount so the limiter applies; otherwise the source volume.
    /// </summary>
    public string EffectiveFsPath(Guid uuid)
    {
        if (_config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota)
            return FuseQuotaLimiter.GetMountPath(_config.System, uuid);

        return DataPath(uuid);
    }

    /// <summary>
    /// Path for interactive file access (SFTP/FTP). Falls back to the source volume when
    /// FuseQuota is configured but the mount socket is unhealthy.
    /// </summary>
    public string ResolveAccessFsPath(Guid uuid, AppLogger? logger = null)
    {
        if (_config.System.EffectiveDiskLimiterMode != DiskLimiterModeKind.FuseQuota)
            return DataPath(uuid);

        var dataPath = DataPath(uuid);
        var limiter = new FuseQuotaLimiter(_config, uuid, dataPath, 0, logger);
        var healthy = limiter.IsSocketFunctionalAsync().GetAwaiter().GetResult()
                      && Directory.Exists(limiter.MountPath);
        if (healthy)
            return limiter.MountPath;

        logger?.Warning(
            LoggerTypes.Disk,
            $"fusequota unhealthy for {uuid}; using DataPath for SFTP/FTP access");
        return dataPath;
    }

    public string GetRuntimeLogs(Guid uuid, int lines = 100, string? query = null, bool regex = false, int searchScanLines = 10_000)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
            return "(static WebSpace no runtime container logs)\n";

        var text = _runtime.GetLogsAsync(uuid, Math.Clamp(searchScanLines, lines, 10_000)).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(query))
            return TailText(text, lines);

        var filtered = ProxyAccessLogs.FilterLines(
            text.Replace("\r\n", "\n").Split('\n'),
            query.Trim(),
            regex);
        return string.Join('\n', filtered.Count <= lines ? filtered : filtered.Skip(filtered.Count - lines));
    }

    public object GetProxyLogs(
        Guid uuid,
        string? domain = null,
        int lines = 200,
        int days = 0,
        string? query = null,
        bool regex = false,
        int searchScanLines = ProxyAccessLogs.DefaultSearchScanLines)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        return ProxyAccessLogs.Read(
            _config.System.RootDirectory,
            space,
            domain,
            lines,
            days,
            query,
            regex,
            searchScanLines);
    }

    public void RotateProxyLogs(Guid uuid, string? domain = null)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        ProxyAccessLogs.RotateSpace(_config.System.RootDirectory, space, domain);
    }

    private static string TailText(string text, int lines)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var parts = text.Replace("\r\n", "\n").Split('\n');
        if (parts.Length <= lines)
            return text;
        return string.Join('\n', parts.AsSpan(parts.Length - lines).ToArray());
    }

    public string GetInstallLogs(Guid uuid)
    {
        _ = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var path = WebSpaceInstaller.InstallLogPath(EffectiveFsPath(uuid));
        if (!File.Exists(path))
            path = WebSpaceInstaller.InstallLogPath(DataPath(uuid));
        if (!File.Exists(path))
            return "(no install log captured)\n";

        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            _logger?.Debug(LoggerTypes.WebSpaces,
                $"Install log read failed for {uuid} ({path}): {ex.Message}");
            return "(install log temporarily unavailable)\n";
        }
    }

    public async IAsyncEnumerable<string> FollowRuntimeLogsAsync(
        Guid uuid,
        int sinceLines = 100,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
        {
            yield return "(static WebSpace no runtime container logs)";
            yield break;
        }

        await foreach (var line in _runtime.FollowLogsAsync(uuid, sinceLines, cancellationToken))
            yield return line;
    }

    /// <summary>Send a console command to the WebSpace runtime stdin.</summary>
    public Task SendConsoleCommandAsync(
        Guid uuid,
        string command,
        CancellationToken cancellationToken = default) =>
        _events.WithHooksAsync(
            new WebSpaceConsoleCommandBeforeEvent { WebSpaceUuid = uuid, Command = command ?? "" },
            err => new WebSpaceConsoleCommandAfterEvent
            {
                WebSpaceUuid = uuid,
                Command = command ?? "",
                Error = err,
            },
            async ct =>
            {
                var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
                    throw new InvalidOperationException("Static WebSpace has no console stdin.");

                await _runtime.SendStdinAsync(uuid, command ?? "", ct);
            },
            cancellationToken);

    /// <summary>
    /// Run a one-shot command in the WebSpace container (docker exec).
    /// For schedule tasks such as WordPress / WHMCS cron.
    /// </summary>
    public Task<(long ExitCode, string Output)> ExecCommandAsync(
        Guid uuid,
        string command,
        CancellationToken cancellationToken = default) =>
        _events.WithHooksAsync(
            new WebSpaceExecBeforeEvent { WebSpaceUuid = uuid, Command = command ?? "" },
            (result, err) => new WebSpaceExecAfterEvent
            {
                WebSpaceUuid = uuid,
                Command = command ?? "",
                ExitCode = result.ExitCode,
                Output = result.Output,
                Error = err,
            },
            async ct =>
            {
                var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
                    throw new InvalidOperationException("Static WebSpace cannot exec schedule commands.");

                return await _runtime.ExecCommandAsync(uuid, space.Runtime, command ?? "", ct);
            },
            cancellationToken);

    /// <summary>Stop runtime, optionally wipe files, re-run install from panel.</summary>
    public WebSpace Reinstall(Guid uuid, bool wipeFiles = true, bool startOnCompletion = false) =>
        _events.WithHooks(
            new WebSpaceReinstallBeforeEvent
            {
                WebSpaceUuid = uuid,
                WipeFiles = wipeFiles,
                StartOnCompletion = startOnCompletion,
            },
            (_, err) => new WebSpaceReinstallAfterEvent { WebSpaceUuid = uuid, Error = err },
            () => ReinstallCore(uuid, wipeFiles, startOnCompletion));

    private WebSpace ReinstallCore(Guid uuid, bool wipeFiles, bool startOnCompletion)
    {
        lock (_mutateGate)
        {
            var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
            var dataPath = DataPath(uuid);
            var fsPath = EffectiveFsPath(uuid);

            space.Status = WebSpaceStatus.Reinstalling;
            space.State = WebSpaceState.Stopped;
            space.UpdatedAt = DateTimeOffset.UtcNow;
            Persist(space);
            SyncPanelState(space);

            try { _runtime.RemoveAsync(uuid).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"reinstall runtime remove: {ex.Message}"); }

            try { _installer.CleanupAsync(uuid).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"reinstall installer cleanup: {ex.Message}"); }

            if (wipeFiles)
                WipeDirectoryContents(fsPath, preserveMeta: true, metaSourcePath: dataPath);

            _spaces[uuid] = space;
            QueueDeferredReinstall(uuid, fsPath, startOnCompletion);
            _logger?.Info(LoggerTypes.WebSpaces, $"Queued reinstall for {uuid}");
            return space;
        }
    }

    private void QueueDeferredReinstall(Guid uuid, string fsPath, bool startOnCompletion)
    {
        if (!_installInFlight.TryAdd(uuid, 0))
            return;

        var cts = new CancellationTokenSource();
        _installTokens[uuid] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunDeferredReinstallAsync(uuid, fsPath, startOnCompletion, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _installInFlight.TryRemove(uuid, out _);
                if (_installTokens.TryRemove(uuid, out var token))
                    token.Dispose();
            }
        });
    }

    private async Task RunDeferredReinstallAsync(Guid uuid, string fsPath, bool startOnCompletion, CancellationToken cancellationToken)
    {
        if (_wsHub is not null)
        {
            await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Reinstalling).ConfigureAwait(false);
            await _wsHub.SendInstallStartedAsync(uuid).ConfigureAwait(false);
        }

        try
        {
            var install = await _panel.FetchWebSpaceInstallAsync(uuid).ConfigureAwait(false);

            WebSpace space;
            lock (_mutateGate)
            {
                space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
            }

            SeedDocumentRoot(space, fsPath);
            await _installer.RunAsync(space, fsPath, install, cancellationToken).ConfigureAwait(false);

            lock (_mutateGate)
            {
                space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
                space.Status = WebSpaceStatus.Installed;
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
                _spaces[uuid] = space;
            }

            await _panel.ReportWebSpaceInstallAsync(uuid, successful: true, reinstall: true).ConfigureAwait(false);
            _logger?.Info(LoggerTypes.WebSpaces, $"Reinstall completed for {uuid}");

            if (_wsHub is not null)
            {
                await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Installed).ConfigureAwait(false);
                await _wsHub.SendInstallCompletedAsync(uuid).ConfigureAwait(false);
            }

            lock (_mutateGate)
            {
                var current = Get(uuid);
                if (current is null)
                    return;

                if (startOnCompletion)
                {
                    try
                    {
                        PowerInternal(current, "start");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warning(LoggerTypes.WebSpaces,
                            $"reinstall start_on_completion failed for {uuid}: {ex.Message}");
                    }
                }

                Persist(current);
                SyncPanelState(current);
                RebuildProxy();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_mutateGate)
            {
                var space = Get(uuid);
                if (space is not null)
                {
                    space.Status = WebSpaceStatus.Failed;
                    space.State = WebSpaceState.Stopped;
                    space.UpdatedAt = DateTimeOffset.UtcNow;
                    Persist(space);
                    SyncPanelState(space);
                }
            }

            try
            {
                await _panel.ReportWebSpaceInstallAsync(uuid, successful: false, reinstall: true)
                    .ConfigureAwait(false);
            }
            catch (Exception reportEx)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to report reinstall abort: {reportEx.Message}");
            }

            if (_wsHub is not null)
            {
                await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Failed).ConfigureAwait(false);
                await _wsHub.SendInstallFailedAsync(uuid, "Reinstall aborted").ConfigureAwait(false);
            }

            _logger?.Info(LoggerTypes.WebSpaces, $"Reinstall aborted for {uuid}");
        }
        catch (Exception ex)
        {
            lock (_mutateGate)
            {
                var space = Get(uuid);
                if (space is not null)
                {
                    space.Status = WebSpaceStatus.Failed;
                    space.State = WebSpaceState.Stopped;
                    space.UpdatedAt = DateTimeOffset.UtcNow;
                    Persist(space);
                    SyncPanelState(space);
                }
            }

            try
            {
                await _panel.ReportWebSpaceInstallAsync(uuid, successful: false, reinstall: true)
                    .ConfigureAwait(false);
            }
            catch (Exception reportEx)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to report reinstall: {reportEx.Message}");
            }

            if (_wsHub is not null)
            {
                await _wsHub.SendStatusAsync(uuid, WebSpaceStatus.Failed).ConfigureAwait(false);
                await _wsHub.SendInstallFailedAsync(uuid, ex.Message).ConfigureAwait(false);
            }

            _logger?.Error(LoggerTypes.WebSpaces, $"Reinstall failed for {uuid}: {ex.Message}");
        }
    }

    private static void WipeDirectoryContents(string path, bool preserveMeta, string? metaSourcePath = null)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (preserveMeta && (name is "webspace.json" or "site.json" or ".install"))
                continue;

            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch
            {
                // best-effort wipe
            }
        }

        // Also wipe .install contents but keep the directory for new logs.
        var installDir = Path.Combine(path, ".install");
        if (Directory.Exists(installDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(installDir))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch { /* best-effort */ }
            }
        }

        _ = metaSourcePath;
    }

    private void PowerInternal(WebSpace space, string action)
    {
        var normalized = action.Trim().ToLowerInvariant();
        var fsPath = EffectiveFsPath(space.Uuid);

        switch (normalized)
        {
            case "start":
                EnsureBackendPort(space);
                _runtime.StartAsync(space, fsPath, space.Startup).GetAwaiter().GetResult();
                break;
            case "stop":
                _runtime.StopAsync(space, kill: false).GetAwaiter().GetResult();
                break;
            case "kill":
                _runtime.StopAsync(space, kill: true).GetAwaiter().GetResult();
                break;
            case "restart":
                EnsureBackendPort(space);
                _runtime.RestartAsync(space, fsPath, space.Startup).GetAwaiter().GetResult();
                break;
            default:
                throw new ArgumentException($"Unknown power action '{action}'.");
        }

        space.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void RebuildProxy()
    {
        foreach (var space in _spaces.Values)
            EnsureBackendPort(space);

        _proxy.Rebuild(_spaces.Values);
        _staticFiles?.Sync(_spaces.Values);
    }

    private void EnsureBackendPort(WebSpace space)
    {
        if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
        {
            var provider = (_config.System.Proxy.Provider ?? "caddy").Trim().ToLowerInvariant();
            var needsLoopback =
                _config.System.Proxy.Enabled &&
                provider == "traefik";

            if (needsLoopback)
            {
                if (space.BackendPort <= 0)
                {
                    space.BackendPort = _ports.Allocate(
                        _spaces.Values.Where(s => s.Uuid != space.Uuid), preferred: 0);
                    _logger?.Info(LoggerTypes.WebSpaces,
                        $"Allocated Traefik static backend_port {space.BackendPort} for {space.Uuid}");
                    try { Persist(space); } catch { /* boot may not have fully loaded */ }
                }

                space.State = WebSpaceState.Running;
                return;
            }

            space.BackendPort = 0;
            space.State = WebSpaceState.Running;
            return;
        }

        if (space.BackendPort > 0)
            return;

        space.BackendPort = _ports.Allocate(_spaces.Values.Where(s => s.Uuid != space.Uuid), preferred: 0);
        _logger?.Info(LoggerTypes.WebSpaces, $"Allocated backend_port {space.BackendPort} for {space.Uuid}");
    }

    private void SyncPanelState(WebSpace space)
    {
        try
        {
            _panel.SyncWebSpaceStateAsync(space.Uuid, space.BackendPort, space.State)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"Panel state sync {space.Uuid}: {ex.Message}");
        }
    }

    private void BroadcastWsStatus(WebSpace space)
    {
        if (_wsHub is null)
            return;

        var payload = space.Status is WebSpaceStatus.Installing or WebSpaceStatus.Reinstalling
            ? space.Status
            : space.State;

        try
        {
            _wsHub.SendStatusAsync(space.Uuid, payload).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.WebSpaces, $"WS status broadcast {space.Uuid}: {ex.Message}");
        }
    }

    private void ReconcileRuntimes()
    {
        if (!_config.Docker.RuntimeReconciliation.Enabled)
            return;

        foreach (var space in _spaces.Values)
        {
            if (space.Status != WebSpaceStatus.Installed)
                continue;

            if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
            {
                space.State = WebSpaceState.Running;
                continue;
            }

            if (space.State != WebSpaceState.Running)
                continue;

            try
            {
                EnsureBackendPort(space);
                _runtime.StartAsync(space, EffectiveFsPath(space.Uuid), space.Startup).GetAwaiter().GetResult();
                Persist(space);
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Reconcile start {space.Uuid}: {ex.Message}");
                space.State = WebSpaceState.Stopped;
                Persist(space);
            }
        }
    }

    private static void SeedDocumentRoot(WebSpace space, string dataPath)
    {
        var rel = NormalizeDocumentRoot(space.DocumentRoot);
        var index = string.IsNullOrEmpty(rel)
            ? Path.Combine(dataPath, "index.html")
            : Path.Combine(dataPath, rel, "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        if (File.Exists(index))
            return;

        File.WriteAllText(index,
            $"""
             <!DOCTYPE html>
             <html lang="en">
             <head><meta charset="utf-8"><title>{space.Name}</title></head>
             <body><h1>{space.Name}</h1><p>WebSpace {space.Uuid} · webplate <code>{space.WebPlateId}</code> ({space.Runtime})</p></body>
             </html>
             """);
    }

    /// <summary>Blank / "." → site root (empty relative path).</summary>
    internal static string NormalizeDocumentRoot(string? documentRoot)
    {
        if (string.IsNullOrWhiteSpace(documentRoot))
            return "";
        var trimmed = documentRoot.Trim().Replace('\\', '/').Trim('/');
        if (trimmed is "" or ".")
            return "";
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == "." || p == ".."))
            return "";
        return string.Join('/', parts);
    }

    internal static List<string> SanitizeDenyIps(IEnumerable<string>? ips)
    {
        var result = new List<string>();
        if (ips is null)
            return result;
        foreach (var raw in ips)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0 || value.Length > 64)
                continue;
            if (!System.Net.IPAddress.TryParse(value.Split('/')[0], out _))
                continue;
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(value);
        }
        return result;
    }

    /// <summary>Normalize URI path denylist; rejects traversal and ACME challenge paths.</summary>
    internal static List<string> SanitizeDenyPaths(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        if (paths is null)
            return result;

        foreach (var raw in paths)
        {
            var value = (raw ?? "").Trim().Replace('\\', '/');
            if (value.Length == 0 || value.Length > 256)
                continue;
            if (!value.StartsWith('/'))
                value = "/" + value;
            if (value.Contains("..", StringComparison.Ordinal) || value.Contains('\0'))
                continue;
            // Never block ACME HTTP-01 challenges.
            if (value.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase))
                continue;
            // Collapse duplicate slashes.
            while (value.Contains("//", StringComparison.Ordinal))
                value = value.Replace("//", "/", StringComparison.Ordinal);
            if (value is "/" or "")
                continue;
            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(value);
        }

        return result;
    }

    /// <summary>Resolve absolute content path; blank document root uses the WebSpace data root.</summary>
    internal static string ResolveContentRootPath(string basePath, string? documentRoot)
    {
        var rel = NormalizeDocumentRoot(documentRoot);
        return string.IsNullOrEmpty(rel) ? basePath : Path.Combine(basePath, rel);
    }

    private void EnsureDomainsAvailable(IEnumerable<string> domains, Guid? except)
    {
        foreach (var domain in domains)
        {
            var clash = _spaces.Values.FirstOrDefault(s =>
                (except is null || s.Uuid != except) &&
                s.Domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)));

            if (clash is not null)
                throw new InvalidOperationException(
                    $"Domain '{domain}' is already assigned to WebSpace {clash.Uuid}.");
        }
    }

    private ulong GetDiskUsed(WebSpace space)
    {
        if (_config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota)
        {
            try
            {
                var limiter = new FuseQuotaLimiter(
                    _config, space.Uuid, DataPath(space.Uuid), space.DiskLimitBytes, _logger);
                return limiter.DiskUsageAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.Disk, $"fusequota usage {space.Uuid}: {ex.Message}");
            }
        }

        return DirectorySize(DataPath(space.Uuid));
    }

    private void LoadFromDisk()
    {
        if (!Directory.Exists(_config.System.Data))
            return;

        foreach (var dir in Directory.EnumerateDirectories(_config.System.Data))
        {
            var metaPath = Path.Combine(dir, "webspace.json");
            var legacy = Path.Combine(dir, "site.json");
            if (!File.Exists(metaPath) && File.Exists(legacy))
                metaPath = legacy;

            if (!File.Exists(metaPath))
                continue;

            try
            {
                var space = JsonSerializer.Deserialize<WebSpace>(File.ReadAllText(metaPath), JsonOptions);
                if (space is null || space.Uuid == Guid.Empty)
                    continue;

                if (string.IsNullOrWhiteSpace(space.State))
                    space.State = space.Runtime == "static" ? WebSpaceState.Running : WebSpaceState.Stopped;

                _spaces[space.Uuid] = space;
                if (metaPath.EndsWith("site.json", StringComparison.Ordinal))
                    Persist(space);
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to load WebSpace meta {metaPath}: {ex.Message}");
            }
        }

        _logger?.Info(LoggerTypes.WebSpaces, $"Loaded {_spaces.Count} WebSpace(s) from {_config.System.Data}");
    }

    private void AttachExistingMounts()
    {
        if (_config.System.EffectiveDiskLimiterMode != DiskLimiterModeKind.FuseQuota)
        {
            _logger?.Debug(LoggerTypes.Disk, "Limiter mode none destroying any leftover fusequota mounts");
            DestroyAllFuseMounts();
            return;
        }

        if (!FuseQuotaLimiter.IsBinaryAvailable(_config.System))
        {
            _logger?.Warning(LoggerTypes.Disk, "FuseQuota mode enabled but binary missing; WebSpaces loaded without mounts.");
            return;
        }

        foreach (var space in _spaces.Values)
        {
            try
            {
                var limiter = new FuseQuotaLimiter(
                    _config, space.Uuid, DataPath(space.Uuid), space.DiskLimitBytes, _logger);
                limiter.Setup();
                limiter.StartupAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.Disk, $"Failed to attach fusequota for {space.Uuid}: {ex.Message}");
            }
        }
    }

    private void DestroyAllFuseMounts()
    {
        var seen = new HashSet<Guid>();
        foreach (var space in _spaces.Values)
        {
            seen.Add(space.Uuid);
            TryDestroyFuseMount(space.Uuid, DataPath(space.Uuid), space.DiskLimitBytes);
        }

        var vmountRoot = _config.System.VmountDirectory;
        if (!Directory.Exists(vmountRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(vmountRoot))
        {
            if (!Guid.TryParse(Path.GetFileName(dir), out var uuid) || !seen.Add(uuid))
                continue;

            TryDestroyFuseMount(uuid, DataPath(uuid), diskLimitBytes: 0);
        }
    }

    private void TryDestroyFuseMount(Guid uuid, string dataPath, long diskLimitBytes)
    {
        try
        {
            var limiter = new FuseQuotaLimiter(_config, uuid, dataPath, diskLimitBytes, _logger);
            limiter.DestroyAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Disk, $"fusequota destroy {uuid}: {ex.Message}");
        }
    }

    public void PersistPublic(WebSpace space)
    {
        lock (_mutateGate)
        {
            _spaces[space.Uuid] = space;
            Persist(space);
        }
    }

    private void Persist(WebSpace space)
    {
        var path = Path.Combine(DataPath(space.Uuid), "webspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(space, JsonOptions));
    }

    private void TryCleanupFailedCreate(Guid uuid, string dataPath, bool useFuse)
    {
        try { _runtime.RemoveAsync(uuid).GetAwaiter().GetResult(); } catch { /* best-effort */ }
        try { _installer.CleanupAsync(uuid).GetAwaiter().GetResult(); } catch { /* best-effort */ }

        try
        {
            if (useFuse)
            {
                var limiter = new FuseQuotaLimiter(_config, uuid, dataPath, 0, _logger);
                limiter.DestroyAsync().GetAwaiter().GetResult();
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (Directory.Exists(dataPath))
                Directory.Delete(dataPath, recursive: true);
        }
        catch { /* best-effort */ }

        _spaces.TryRemove(uuid, out _);
    }

    private static List<string> NormalizeDomains(IEnumerable<string>? domains) =>
        domains?
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant().TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    private static List<WebSpaceDomainRoute> NormalizeDomainRoutes(
        IEnumerable<PanelDomainRoute>? routes,
        IReadOnlyList<string> fallbackDomains)
    {
        var normalized = new List<WebSpaceDomainRoute>();
        if (routes is not null)
        {
            foreach (var route in routes)
            {
                if (route is null || string.IsNullOrWhiteSpace(route.Domain))
                    continue;
                var domain = route.Domain.Trim().ToLowerInvariant().TrimEnd('.');
                var type = (route.Type ?? "alias").Trim().ToLowerInvariant() switch
                {
                    "primary" => "primary",
                    "redirect" => "redirect",
                    _ => "alias",
                };
                normalized.Add(new WebSpaceDomainRoute
                {
                    Domain = domain,
                    Type = type,
                    RedirectTarget = string.IsNullOrWhiteSpace(route.RedirectTarget)
                        ? null
                        : route.RedirectTarget.Trim(),
                    DocumentRoot = NormalizeDocumentRoot(route.DocumentRoot),
                });
            }
        }

        if (normalized.Count == 0)
        {
            foreach (var domain in fallbackDomains)
            {
                if (string.IsNullOrWhiteSpace(domain))
                    continue;
                normalized.Add(new WebSpaceDomainRoute
                {
                    Domain = domain.Trim().ToLowerInvariant().TrimEnd('.'),
                    Type = normalized.Count == 0 ? "primary" : "alias",
                });
            }
        }

        if (normalized.Count > 0 && normalized.All(r => r.Type != "primary"))
            normalized[0].Type = "primary";

        return normalized
            .GroupBy(r => r.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string NormalizeSslMode(string? mode)
    {
        if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase))
            return "custom";
        if (string.Equals(mode, "dns01", StringComparison.OrdinalIgnoreCase))
            return "dns01";
        return "acme";
    }

    private static string NormalizeAcmeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "" : email.Trim();

    private static string NormalizeBackendHost(string? host) =>
        string.IsNullOrWhiteSpace(host) ? "" : host.Trim();

    private static ulong DirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        ulong total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += (ulong)new FileInfo(file).Length; }
                catch { /* skip */ }
            }
        }
        catch { /* incomplete */ }

        return total;
    }
}
