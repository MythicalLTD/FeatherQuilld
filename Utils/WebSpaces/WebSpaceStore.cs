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
    private WebSpaceScheduleManager? _schedules;
    private readonly ConcurrentDictionary<Guid, WebSpace> _spaces = new();
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
        IEventBus? events = null)
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
            space.UpdatedAt);

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
            if (_spaces.ContainsKey(request.Uuid))
                throw new InvalidOperationException($"WebSpace {request.Uuid} already exists on this node.");

            _logger?.Info(LoggerTypes.WebSpaces, $"Fetching WebSpace {request.Uuid} from panel…");
            var remote = _panel.FetchWebSpaceAsync(request.Uuid).GetAwaiter().GetResult();
            if (remote.Uuid == Guid.Empty)
                remote.Uuid = request.Uuid;

            var domains = NormalizeDomains(remote.Domains);
            foreach (var domain in domains)
            {
                if (!WebSpaceValidation.IsValidDomain(domain))
                    throw new ArgumentException($"Invalid domain '{domain}' from panel.");
            }

            EnsureDomainsAvailable(domains, except: null);

            var diskBytes = remote.Build?.DiskSpace > 0
                ? remote.Build.DiskSpace * 1024L * 1024L
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
                Domains = domains,
                Ssl = remote.Ssl,
                BackendPort = remote.BackendPort,
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
                    try
                    {
                        var install = _panel.FetchWebSpaceInstallAsync(space.Uuid).GetAwaiter().GetResult();
                        if (!string.IsNullOrWhiteSpace(install.ContainerImage))
                            space.ContainerImage = install.ContainerImage.Trim();

                        SeedDocumentRoot(space, fsPath);
                        _installer.RunAsync(space, fsPath, install).GetAwaiter().GetResult();

                        space.Status = WebSpaceStatus.Installed;
                        space.UpdatedAt = DateTimeOffset.UtcNow;
                        Persist(space);
                        _panel.ReportWebSpaceInstallAsync(space.Uuid, successful: true).GetAwaiter().GetResult();
                        _logger?.Info(LoggerTypes.WebSpaces,
                            $"Install completed for {space.Uuid} image={space.ContainerImage}");
                    }
                    catch (Exception ex)
                    {
                        space.Status = WebSpaceStatus.Failed;
                        space.State = WebSpaceState.Stopped;
                        Persist(space);
                        try
                        {
                            _panel.ReportWebSpaceInstallAsync(space.Uuid, successful: false)
                                .GetAwaiter().GetResult();
                        }
                        catch (Exception reportEx)
                        {
                            _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to report install: {reportEx.Message}");
                        }

                        throw new InvalidOperationException($"Install failed: {ex.Message}", ex);
                    }
                }
                else
                {
                    SeedDocumentRoot(space, fsPath);
                    space.Status = WebSpaceStatus.Installed;
                    space.UpdatedAt = DateTimeOffset.UtcNow;
                    Persist(space);
                    _panel.ReportWebSpaceInstallAsync(space.Uuid, successful: true).GetAwaiter().GetResult();
                }

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

    /// <summary>Pull latest panel config and apply domains, ssl, disk, document_root, proxy.</summary>
    public WebSpace ApplyConfigFromPanel(Guid uuid) =>
        _events.WithHooks(
            new WebSpaceSyncBeforeEvent { WebSpaceUuid = uuid },
            (_, err) => new WebSpaceSyncAfterEvent { WebSpaceUuid = uuid, Error = err },
            () => ApplyConfigFromPanelCore(uuid));

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
            foreach (var domain in domains)
            {
                if (!WebSpaceValidation.IsValidDomain(domain))
                    throw new ArgumentException($"Invalid domain '{domain}' from panel.");
            }

            EnsureDomainsAvailable(domains, except: uuid);

            var diskBytes = remote.Build?.DiskSpace > 0
                ? remote.Build.DiskSpace * 1024L * 1024L
                : space.DiskLimitBytes;

            space.Name = string.IsNullOrWhiteSpace(remote.Name) ? space.Name : remote.Name.Trim();
            space.Domains = domains;
            space.Ssl = remote.Ssl;
            space.DiskLimitBytes = diskBytes;
            space.DocumentRoot = remote.Meta is null
                ? space.DocumentRoot
                : NormalizeDocumentRoot(remote.Meta.DocumentRoot);
            space.UpdatedAt = DateTimeOffset.UtcNow;

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
            var useFuse = _config.System.EffectiveDiskLimiterMode == DiskLimiterModeKind.FuseQuota;

            try { _runtime.RemoveAsync(uuid).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"runtime remove: {ex.Message}"); }

            try { _installer.CleanupAsync(uuid).GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger?.Warning(LoggerTypes.WebSpaces, $"installer cleanup: {ex.Message}"); }

            if (useFuse)
            {
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
            provider = _config.System.Proxy.Provider,
            acme_email = _config.System.Proxy.AcmeEmail,
            domains,
        };
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
        if (provider == "nginx")
        {
            if (_acme is null)
                throw new InvalidOperationException("ACME service is not available on this node.");
            if (string.IsNullOrWhiteSpace(_config.System.Proxy.AcmeEmail))
                throw new InvalidOperationException("Web node acme_email is not configured.");

            await _acme.EnsureCertsAsync(space.Domains, cancellationToken, force: true);
        }

        lock (_mutateGate)
        {
            RebuildProxy();
        }

        return GetSslStatus(uuid);
    }

    private static bool ProbeCaddyCert(string domain)
    {
        try
        {
            // Caddy stores certs under ~/.local/share/caddy or /var/lib/caddy — best-effort probe.
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

    public string GetRuntimeLogs(Guid uuid, int lines = 100)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
            return "(static WebSpace — no runtime container logs)\n";

        return _runtime.GetLogsAsync(uuid, lines).GetAwaiter().GetResult();
    }

    public string GetInstallLogs(Guid uuid)
    {
        _ = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        var path = WebSpaceInstaller.InstallLogPath(EffectiveFsPath(uuid));
        if (!File.Exists(path))
            path = WebSpaceInstaller.InstallLogPath(DataPath(uuid));
        if (!File.Exists(path))
            return "(no install log captured)\n";
        return File.ReadAllText(path);
    }

    public async IAsyncEnumerable<string> FollowRuntimeLogsAsync(
        Guid uuid,
        int sinceLines = 100,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var space = Get(uuid) ?? throw new InvalidOperationException($"WebSpace {uuid} not found.");
        if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
        {
            yield return "(static WebSpace — no runtime container logs)";
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

            try
            {
                var install = _panel.FetchWebSpaceInstallAsync(space.Uuid).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(install.ContainerImage))
                    space.ContainerImage = install.ContainerImage.Trim();

                SeedDocumentRoot(space, fsPath);
                _installer.RunAsync(space, fsPath, install).GetAwaiter().GetResult();

                space.Status = WebSpaceStatus.Installed;
                space.UpdatedAt = DateTimeOffset.UtcNow;
                Persist(space);
                _panel.ReportWebSpaceInstallAsync(space.Uuid, successful: true, reinstall: true)
                    .GetAwaiter().GetResult();

                if (startOnCompletion)
                {
                    try { PowerInternal(space, "start"); }
                    catch (Exception ex)
                    {
                        _logger?.Warning(LoggerTypes.WebSpaces,
                            $"reinstall start_on_completion failed for {space.Uuid}: {ex.Message}");
                    }
                }

                Persist(space);
                SyncPanelState(space);
                RebuildProxy();
                _logger?.Info(LoggerTypes.WebSpaces, $"Reinstall completed for {space.Uuid}");
                return space;
            }
            catch (Exception ex)
            {
                space.Status = WebSpaceStatus.Failed;
                space.State = WebSpaceState.Stopped;
                Persist(space);
                try
                {
                    _panel.ReportWebSpaceInstallAsync(space.Uuid, successful: false, reinstall: true)
                        .GetAwaiter().GetResult();
                }
                catch (Exception reportEx)
                {
                    _logger?.Warning(LoggerTypes.WebSpaces, $"Failed to report reinstall: {reportEx.Message}");
                }

                throw new InvalidOperationException($"Reinstall failed: {ex.Message}", ex);
            }
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
        {
            if (!WebSpaceRuntime.NeedsContainer(space.Runtime))
                EnsureBackendPort(space);
        }

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
        var trimmed = documentRoot.Trim().Trim('/');
        return trimmed is "" or "." ? "" : trimmed;
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
                if (limiter.IsSocketFunctionalAsync().GetAwaiter().GetResult())
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
            _logger?.Debug(LoggerTypes.Disk, "Skipping fusequota attach (limiter mode none)");
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
