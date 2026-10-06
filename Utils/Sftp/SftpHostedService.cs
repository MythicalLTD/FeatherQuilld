using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using d0x2a.EmbeddedSsh;
using d0x2a.EmbeddedSsh.Auth;
using d0x2a.EmbeddedSsh.Connection;
using d0x2a.EmbeddedSsh.HostKeys;
using d0x2a.EmbeddedSsh.Protocol.Messages;
using FxSsh;
using FxSsh.Services;
using FeatherQuilld.Plugins.Events;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.WebSpaces;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using EmbeddedSshServer = d0x2a.EmbeddedSsh.SshServer;
using FxSshServer = FxSsh.SshServer;

namespace FeatherQuilld.Utils.Sftp;

/// <summary>
/// In-process SSH/SFTP server. Uses FxSsh for <c>ssh-rsa</c> host keys and
/// EmbeddedSsh for real OpenSSH <c>ssh-ed25519</c> host keys.
/// </summary>
public sealed class SftpHostedService : IHostedService, IDisposable
{
    private readonly AppConfig _config;
    private readonly WebSpaceStore _spaces;
    private readonly IPanelClient _panel;
    private readonly AppLogger? _logger;
    private readonly IEventBus _events;
    private readonly ConcurrentDictionary<string, SftpAuthResult> _authBySession = new();
    // Per-connection: SSH LocalChannelId restarts at 0 on every session, so a
    // process-wide map keyed only by channel id would leak and cross-contaminate
    // concurrent clients. Entries are dropped when the connection handler ends.
    private readonly ConcurrentDictionary<SshConnection, ConcurrentDictionary<uint, byte>> _sftpChannelsByConnection = new();

    // Registration handshake between the connection handler and the panel
    // authenticator (which runs on the library's own dispatch thread): the
    // handler completes the per-session signal once it is committed to
    // busy-spinning for the connection layer, and the authenticator's success
    // path WAITS for that signal before returning Success. That guarantees the
    // spin is already running before the library constructs the connection
    // layer and dispatches the client's pipelined channel-open + "subsystem"
    // request, so the hook can never be attached too late (see
    // WaitForAuthAndHookSubsystem).
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _spinningSignals = new();

    // Per-connection attempt accounting for the panel's per-method auth
    // limits (password / publickey). The library's own MaxAuthAttempts counts
    // EVERY request (none probe, key probe, password) and must not be used
    // for these limits.
    private readonly SftpAuthAttemptLimiter _attemptLimiter = new();

    // The post-auth subsystem hook needs a short busy-spin (see
    // WaitForAuthAndHookSubsystem), but unbounded concurrent spinners
    // could starve the thread pool — cap them at 2×cores, clamped to [8, 32].
    private static readonly SemaphoreSlim SubsystemHookSpinGate = new(
        Math.Clamp(2 * Environment.ProcessorCount, 8, 32));

    private static readonly TimeSpan SubsystemHookSpinBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpinningHandshakeBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HandshakeWaitBudget = TimeSpan.FromSeconds(10);
    private FxSshServer? _fxServer;
    private EmbeddedSshServer? _embeddedServer;
    private CancellationTokenSource? _embeddedCts;

    public SftpHostedService(
        AppConfig config,
        WebSpaceStore spaces,
        IPanelClient panel,
        AppLogger? logger = null,
        IEventBus? events = null)
    {
        _config = config;
        _spaces = spaces;
        _panel = panel;
        _logger = logger;
        _events = events.OrNoOp();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Sftp.Enabled)
        {
            _logger?.Info(LoggerTypes.Application, "SFTP disabled");
            return Task.CompletedTask;
        }

        var material = SftpHostKeys.EnsureHostKey(_config, _logger);
        if (material.Algorithm == SftpHostKeys.AlgoEd25519)
            StartEmbeddedEd25519(material);
        else
            StartFxSshRsa(material);

        _logger?.Info(LoggerTypes.Application,
            $"SFTP listening on 0.0.0.0:{_config.Sftp.Port} host_key={material.Algorithm}" +
            (material.FingerprintSha256 is null ? "" : $" fingerprint={material.FingerprintSha256}"));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _fxServer?.Stop(); } catch { /* ignore */ }
        try { _embeddedCts?.Cancel(); } catch { /* ignore */ }

        if (_embeddedServer is not null)
        {
            try { await _embeddedServer.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
            try { await _embeddedServer.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            _embeddedServer = null;
        }
    }

    public void Dispose()
    {
        try { _fxServer?.Stop(); } catch { /* ignore */ }
        _fxServer?.Dispose();
        try { _embeddedCts?.Cancel(); } catch { /* ignore */ }
        _embeddedCts?.Dispose();
        if (_embeddedServer is not null)
        {
            try { _embeddedServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* ignore */ }
        }
    }

    private void StartFxSshRsa(SftpHostKeys.HostKeyMaterial material)
    {
        var keyXml = File.ReadAllText(material.PrivateKeyPath);
        var info = new StartingInfo(IPAddress.Any, _config.Sftp.Port, "SSH-2.0-FeatherQuilld");
        _fxServer = new FxSshServer(info);
        _fxServer.AddHostKey(SftpHostKeys.AlgoRsa, keyXml);
        _fxServer.ConnectionAccepted += OnFxConnectionAccepted;
        _fxServer.ExceptionRasied += (_, ex) =>
            _logger?.Warning(LoggerTypes.Application, $"SFTP exception: {ex.Message}");
        try
        {
            _fxServer.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"SFTP port {_config.Sftp.Port} is already in use. Stop the other process or change sftp.port in config.yml.",
                ex);
        }
    }

    private void StartEmbeddedEd25519(SftpHostKeys.HostKeyMaterial material)
    {
        var hostKey = Ed25519HostKey.FromOpenSshFile(material.PrivateKeyPath);
        var options = new SshServerOptions
        {
            ServerVersion = "SSH-2.0-FeatherQuilld",
            Authenticator = new PanelSftpAuthenticator(this),
            // The panel's password-attempt limit (default 3) must NOT be
            // handed to the library 1:1: the library counts EVERY auth
            // request against MaxAuthAttempts — the "none" probe, each
            // publickey probe, and then the password — so a limit of 3 is
            // exhausted exactly by a successful key-first login, and the
            // library throws right after sending USERAUTH_SUCCESS, killing
            // the connection (FileZilla-style key-first clients failed
            // 100% of the time). Budget for every legitimate request with
            // headroom; the per-method limits are enforced separately in
            // PanelSftpAuthenticator via SftpAuthAttemptLimiter, which
            // counts only the method each limit governs.
            MaxAuthAttempts = Math.Max(20,
                _config.Sftp.Limits.AuthenticationPubkeyAttempts
                + _config.Sftp.Limits.AuthenticationPasswordAttempts
                + 4),
        };
        options.HostKeys.Add(hostKey);

        _embeddedServer = new EmbeddedSshServer(options, new IPEndPoint(IPAddress.Any, _config.Sftp.Port));
        _embeddedCts = new CancellationTokenSource();
        var ct = _embeddedCts.Token;

        _embeddedServer.ConnectionAccepted += connection =>
        {
            // Dedicated background thread — NOT the thread pool. The handler must
            // actually be running (polling the connection state) while authentication
            // is in flight so it can attach the subsystem hook the instant the library
            // constructs the connection layer; a thread-pool task can sit queued for the
            // entire auth window under load, and the client's pipelined subsystem request
            // then races past the hook ("subsystem request failed on channel 0").
            var thread = new Thread(() => _ = HandleEmbeddedConnectionAsync(connection, ct))
            {
                IsBackground = true,
                Name = "fq-sftp-ed25519-handler",
            };
            thread.Start();
            return Task.CompletedTask;
        };

        try
        {
            _embeddedServer.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"SFTP port {_config.Sftp.Port} is already in use. Stop the other process or change sftp.port in config.yml.",
                ex);
        }
    }

    private async Task HandleEmbeddedConnectionAsync(SshConnection connection, CancellationToken ct)
    {
        try
        {
            // IMPORTANT: this handler runs on a dedicated thread, started from the
            // ConnectionAccepted event, which SshServer raises *before* it calls
            // connection.RunAsync(). RunAsync performs the SSH handshake and
            // authentication and only *afterwards* constructs the connection's internal
            // connection layer, then starts dispatching subsequent SSH messages
            // (channel-open, channel-request "subsystem", ...) synchronously right
            // after, with no yield point in between. Until the connection layer exists,
            // HookSubsystemRequests has nothing to attach to, and AcceptChannelAsync
            // throws InvalidOperationException. Both failures were completely silent —
            // that is why SFTP used to die right after "auth ok" with no log line and no
            // exception at all: "subsystem request failed on channel 0" on the client
            // side.
            //
            // We deliberately wait for the *same* private "_connectionLayer" field that
            // HookSubsystemRequests needs, rather than polling the public
            // SshConnection.Channels property (which reads a different field,
            // _channelManager) and only then doing a second, separate reflection lookup.
            // Both fields are assigned back-to-back with no memory barrier in between,
            // so on relaxed/weak read orderings a second reader thread is not guaranteed
            // to observe them becoming visible in the same order — polling Channels can
            // flip true while _connectionLayer is still not observably non-null yet,
            // causing a spurious "connection layer not available" failure right after the
            // wait supposedly succeeded. Waiting on the exact field we are about to use
            // removes that gap entirely.
            //
            // The wait is structured so that CPU is only spent in the one window where it
            // actually matters: while the connection handshakes we sleep-poll the public
            // connection State machine (~0 CPU for scanners, which never reach the auth
            // phase), and once the connection is entering authentication we register with
            // the authenticator (running on the library's own dispatch thread) and
            // busy-spin for the connection layer. The authenticator WAITS for that
            // registration before returning Success, which makes the spin deterministically
            // precede the layer construction/dispatch — see WaitForAuthAndHookSubsystem.
            var (waited, hooked) = WaitForAuthAndHookSubsystem(connection, ct);

            if (!waited)
            {
                _logger?.Warning(LoggerTypes.Application,
                    "SFTP ed25519 connection: key exchange or authentication did not complete in time; closing handler.");
                return;
            }

            if (!hooked)
            {
                _logger?.Warning(LoggerTypes.Application,
                    "SFTP ed25519 connection: could not hook subsystem requests; SFTP subsystem will fail for this connection.");
            }

            // Auth is bound via AuthenticatedUser.Properties["sftp_auth"] and/or
            // conn:{SessionId} — never by username (avoids cross-session confusion).
            try
            {
                if (connection.User?.Properties is not null
                    && connection.User.Properties.TryGetValue("sftp_auth", out var boxed)
                    && boxed is SftpAuthResult bound
                    && connection.SessionId is { Length: > 0 } sid)
                {
                    _authBySession["conn:" + Convert.ToHexString(sid.ToArray())] = bound;
                }
            }
            catch { /* SessionId may not be ready yet; Properties path still works */ }

            while (!ct.IsCancellationRequested)
            {
                SshChannel channel;
                try
                {
                    channel = await connection.AcceptChannelAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Warning(LoggerTypes.Application, $"SFTP ed25519 accept channel failed: {ex}");
                    break;
                }

                _ = Task.Run(() => HandleEmbeddedChannelAsync(connection, channel, ct), ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"SFTP ed25519 connection ended: {ex}");
        }
        finally
        {
            _sftpChannelsByConnection.TryRemove(connection, out _);
            try
            {
                var sessionKey = Convert.ToHexString(connection.SessionId.ToArray());
                _authBySession.TryRemove("conn:" + sessionKey, out _);
                _spinningSignals.TryRemove(sessionKey, out _);
                _attemptLimiter.Remove(sessionKey);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Waits for the connection to reach its authentication phase WITHOUT spending CPU
    /// on peers that never get there, registers with the authenticator, then briefly
    /// busy-spins for the connection's private "_connectionLayer" field and hooks the
    /// "sftp" subsystem channel request on it via reflection.
    ///
    /// Why this shape: the library constructs the connection layer only after
    /// authentication completes (SshConnection.RunAsync: AuthenticateAsync, then
    /// "_connectionLayer = new ConnectionLayer(...)"), and it dispatches the client's
    /// pipelined channel-open + "subsystem" request right after, with no yield point
    /// in between. Whatever thread attaches the hook must therefore already be
    /// spinning at the moment the field is assigned — being woken by any kind of
    /// signal or continuation at that point is too late: the dispatcher can win the
    /// wake-up race and answer the subsystem request before the hook lands
    /// ("subsystem request failed on channel 0").
    ///
    /// Deterministic ordering without burning CPU on the whole handshake:
    ///  1. Sleep-poll the connection's public State machine until it enters
    ///     "Authenticating" (set right before the auth loop). Real clients pipeline
    ///     version+KEXINIT+NEWKEYS and reach it within milliseconds; scanners never
    ///     do, and sleep-polling costs them ~0 CPU.
    ///  2. Complete the per-session registration signal. The panel authenticator —
    ///     which runs ON the library's dispatch thread — WAITS for this signal on
    ///     its success path before returning Success. So by the time the library
    ///     proceeds to construct the connection layer, this method is already inside
    ///     its busy-spin below; observation/scheduling latency is absorbed by the
    ///     authenticator instead of being a race.
    ///  3. Spin (bounded, concurrency-capped, priority-boosted best-effort) for the
    ///     field, then hook it. The spin starts before the layer exists and catches
    ///     the assignment within microseconds — before the client's channel-open can
    ///     even arrive (it needs the USERAUTH_SUCCESS round trip first).
    ///
    /// Returns <c>waited=false</c> when the connection never reached authentication in
    /// time (scanner/idle peer) — the caller should give up on it. <paramref
    /// name="hooked"/> reports whether the hook attach itself succeeded.
    /// </summary>
    private (bool Waited, bool Hooked) WaitForAuthAndHookSubsystem(
        SshConnection connection, CancellationToken ct)
    {
        // Phase 1: sleep-poll until the auth phase is entered. This must stay a
        // sleep (not a spin): a scanner that stalls before auth would otherwise
        // burn CPU for the whole handshake budget on this dedicated thread.
        var deadline = DateTime.UtcNow + HandshakeWaitBudget;
        while (connection.State != ConnectionState.Authenticating
               && connection.State != ConnectionState.Connected
               && connection.State != ConnectionState.Disconnected)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow >= deadline)
                return (false, false);
            Thread.Sleep(1);
        }

        if (connection.State == ConnectionState.Disconnected)
            return (false, false);

        _logger?.Debug(LoggerTypes.Application,
            $"SFTP ed25519 handler: auth phase observed (state={connection.State}) on handler thread #{Environment.CurrentManagedThreadId}");

        var field = typeof(SshConnection).GetField(
            "_connectionLayer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            _logger?.Warning(LoggerTypes.Application,
                "SFTP subsystem hook failed: _connectionLayer field not found via reflection (library layout changed?).");
            return (true, false);
        }

        // Registration handshake: the per-session signal is completed INSIDE the
        // spin loop below (first iteration), so it is only set once this thread is
        // already hot and actively spinning. The authenticator's success path waits
        // for exactly this signal (see PanelSftpAuthenticator) before returning
        // Success — from the signal to the library's connection-layer construction
        // are microseconds on the dispatch thread, and this handler is mid-loop the
        // whole time, so the field read cannot miss the assignment (except by an
        // OS preemption right in that window, which the loop's 2s budget survives).
        var sessionKey = connection.SessionId.Length > 0
            ? Convert.ToHexString(connection.SessionId.ToArray())
            : "";

        // Cap concurrent spinners when a slot is available. Wait(0) only — never
        // BLOCK here: under gate saturation the spin proceeds ungated (still bounded
        // by the spin budget). The cap is CPU protection; correctness must never
        // queue behind it.
        var gated = SubsystemHookSpinGate.Wait(0);

        try
        {
            ThreadPriority? previous = null;
            try
            {
                // Best-effort boost so this spinner wins the race against the
                // connection's own dispatch loop; not available on every platform.
                previous = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch
            {
                // ignore — priority boost is an optimization, never a requirement
            }

            var spinDeadline = DateTime.UtcNow + SubsystemHookSpinBudget;
            var registered = false;
            ConnectionLayer? layer;
            while ((layer = field.GetValue(connection) as ConnectionLayer) is null)
            {
                if (!registered)
                {
                    registered = true;
                    if (sessionKey.Length > 0)
                    {
                        _spinningSignals.GetOrAdd(sessionKey,
                            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                            .TrySetResult();
                    }
                    _logger?.Debug(LoggerTypes.Application,
                        $"SFTP ed25519 handler: registered spinning signal session={sessionKey[..Math.Min(8, sessionKey.Length)]}… (spin running) t={DateTime.UtcNow:HH:mm:ss.fff}");
                }

                if (connection.State == ConnectionState.Disconnected
                    || ct.IsCancellationRequested
                    || DateTime.UtcNow >= spinDeadline)
                {
                    break;
                }

                // Deliberately a REAL busy-wait: Thread.SpinWait only issues PAUSE
                // instructions, it never yields or sleeps. SpinWait.SpinOnce must NOT
                // be used here — after a few iterations it backs off to Thread.Sleep(1),
                // and a Sleep(1) wake is ~1 ms while the window between the library's
                // connection-layer construction and the dispatch of the client's
                // pipelined channel requests is tens of microseconds. A backed-off
                // spinner wakes too late and the hook lands after the dispatch
                // ("subsystem request failed on channel 0" — the original race).
                Thread.SpinWait(30);
            }

            try
            {
                if (previous is { } p)
                    Thread.CurrentThread.Priority = p;
            }
            catch
            {
                // ignore
            }

            if (layer is null)
            {
                _logger?.Warning(LoggerTypes.Application,
                    "SFTP ed25519 connection: connection layer did not appear after auth; subsystem hook skipped.");
                return (true, false);
            }

            var hooked = HookSubsystemRequests(layer, connection);
            _logger?.Debug(LoggerTypes.Application,
                $"SFTP ed25519 handler: hook attach {(hooked ? "succeeded" : "FAILED")} after spin t={DateTime.UtcNow:HH:mm:ss.fff}");
            return (true, hooked);
        }
        finally
        {
            if (gated)
                SubsystemHookSpinGate.Release();
        }
    }

    private bool HookSubsystemRequests(ConnectionLayer layer, SshConnection connection)
    {
        try
        {
            var sftpChannels = _sftpChannelsByConnection.GetOrAdd(
                connection, static _ => new ConcurrentDictionary<uint, byte>());

            layer.ChannelRequestReceived += (channel, request, _) =>
            {
                _logger?.Debug(LoggerTypes.Application,
                    $"SFTP ed25519 hook: channel request type='{request.RequestType}' recipient={request.RecipientChannel} t={DateTime.UtcNow:HH:mm:ss.fff}");

                if (!string.Equals(request.RequestType, "subsystem", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(false);

                var name = ReadSshString(request.RequestData.Span);
                if (!string.Equals(name, "sftp", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(false);

                sftpChannels[channel.LocalChannelId] = 1;
                if (channel.Environment is not null)
                    channel.Environment["featherquilld.subsystem"] = "sftp";
                return ValueTask.FromResult(true);
            };
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"SFTP subsystem hook failed: {ex}");
            return false;
        }
    }

    private async Task HandleEmbeddedChannelAsync(
        SshConnection connection,
        SshChannel channel,
        CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
            {
                if (IsSftpChannel(connection, channel))
                    break;
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            if (!IsSftpChannel(connection, channel))
            {
                await channel.CloseAsync(ct).ConfigureAwait(false);
                return;
            }

            if (!TryGetEmbeddedAuth(connection, out var auth) || string.IsNullOrWhiteSpace(auth.RootPath))
            {
                await channel.CloseAsync(ct).ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(auth.RootPath);
            await using var transport = new EmbeddedSshTransportChannel(channel);
            var sessionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Closed += (_, _) => sessionEnded.TrySetResult();
            _ = OpenSession(transport, auth, auth.User);
            _logger?.Debug(LoggerTypes.Application, $"SFTP subsystem attached root={auth.RootPath}");

            // Only now that the session has subscribed to DataReceived may the
            // read pump start — starting it earlier would let it consume (and
            // silently drop) the client's INIT packet, which may already be
            // buffered on the channel, and the session would wait forever.
            transport.Start();

            // The session ends when the client closes the channel OR sends
            // channel EOF (the transport pump raises Closed on EOF). Both must
            // complete the close handshake below.
            while (!ct.IsCancellationRequested && !channel.IsClosed && !sessionEnded.Task.IsCompleted)
                await Task.Delay(250, ct).ConfigureAwait(false);

            // Session ended: complete the close handshake so the client sees a
            // clean teardown. Without this nobody ever closes the channel — the
            // library's ReadAsync ignores EOF, so nothing reacts to the client's
            // "bye", and one-shot (scripted) logins hang on disconnect for the
            // client's full timeout.
            try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            // shutting down — still attempt the close handshake, best effort
            try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"SFTP ed25519 channel failed: {ex}");
            try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }
        finally
        {
            if (_sftpChannelsByConnection.TryGetValue(connection, out var channels))
                channels.TryRemove(channel.LocalChannelId, out _);
        }
    }

    private bool TryGetEmbeddedAuth(SshConnection connection, out SftpAuthResult auth)
    {
        auth = null!;

        if (connection.User?.Properties is not null
            && connection.User.Properties.TryGetValue("sftp_auth", out var boxed)
            && boxed is SftpAuthResult fromUser)
        {
            auth = fromUser;
            return true;
        }

        try
        {
            var key = "conn:" + Convert.ToHexString(connection.SessionId.ToArray());
            if (_authBySession.TryGetValue(key, out auth!))
                return true;
        }
        catch { /* SessionId may throw if not ready */ }

        return false;
    }

    private bool IsSftpChannel(SshConnection connection, SshChannel channel)
    {
        if (_sftpChannelsByConnection.TryGetValue(connection, out var channels)
            && channels.ContainsKey(channel.LocalChannelId))
            return true;

        if (channel.Environment is not null
            && channel.Environment.TryGetValue("featherquilld.subsystem", out var sub)
            && string.Equals(sub, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cmd = channel.Command?.Trim() ?? "";
        return cmd.Equals("sftp", StringComparison.OrdinalIgnoreCase)
               || cmd.Contains("sftp", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSshString(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return "";
        var len = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (len == 0 || data.Length < 4 + (int)len)
            return "";
        return Encoding.UTF8.GetString(data.Slice(4, (int)len));
    }

    private void OnFxConnectionAccepted(object? sender, Session session)
    {
        session.ServiceRegistered += OnFxServiceRegistered;
        session.Disconnected += (_, _) =>
        {
            var id = Convert.ToHexString(session.SessionId ?? []);
            _authBySession.TryRemove(id, out _);
        };
    }

    private void OnFxServiceRegistered(object? sender, SshService service)
    {
        if (service is UserauthService auth)
            auth.Userauth += OnFxUserAuth;
        else if (service is ConnectionService conn)
            conn.CommandOpened += OnFxCommandOpened;
    }

    private void OnFxUserAuth(object? sender, UserauthArgs e)
    {
        try
        {
            if (_config.Sftp.DisablePasswordAuth
                && string.Equals(e.AuthMethod, "password", StringComparison.OrdinalIgnoreCase))
            {
                e.Result = false;
                return;
            }

            var authMethod = e.AuthMethod ?? "password";
            string? publicKey = null;
            if (e.Key is { Length: > 0 })
            {
                authMethod = "public_key";
                publicKey = Convert.ToBase64String(e.Key);
            }

            var result = Authenticate(authMethod, e.Username ?? "", e.Password ?? "", publicKey);
            if (result is null)
            {
                e.Result = false;
                return;
            }

            var id = Convert.ToHexString(e.Session.SessionId ?? []);
            _authBySession[id] = result;
            e.Result = true;
            _logger?.Info(LoggerTypes.Application,
                $"SFTP auth ok user={e.Username} webspace={result.Server}");
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"SFTP auth failed: {ex.Message}");
            e.Result = false;
        }
    }

    private void OnFxCommandOpened(object? sender, CommandRequestedArgs e)
    {
        var shell = e.ShellType?.Trim().ToLowerInvariant() ?? "";
        var cmd = e.CommandText?.Trim() ?? "";

        if (shell is not ("subsystem" or "exec")
            || (!cmd.Equals("sftp", StringComparison.OrdinalIgnoreCase)
                && !cmd.Contains("sftp", StringComparison.OrdinalIgnoreCase)))
        {
            try { e.Channel.SendClose(); } catch { /* ignore */ }
            return;
        }

        var session = e.AttachedUserauthArgs?.Session;
        var id = session is null ? "" : Convert.ToHexString(session.SessionId ?? []);
        if (!_authBySession.TryGetValue(id, out var auth) || string.IsNullOrWhiteSpace(auth.RootPath))
        {
            try { e.Channel.SendClose(); } catch { /* ignore */ }
            return;
        }

        Directory.CreateDirectory(auth.RootPath);
        try
        {
            _ = OpenSession(e.Channel, auth, e.AttachedUserauthArgs?.Username);
            _logger?.Debug(LoggerTypes.Application, $"SFTP subsystem attached root={auth.RootPath}");
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"Failed to attach rooted SFTP: {ex}");
            try { e.Channel.SendClose(); } catch { /* ignore */ }
        }
    }

    private SftpAuthResult? Authenticate(string authMethod, string username, string password, string? publicKey)
    {
        try
        {
            return _events.WithHooks(
                new SftpAuthBeforeEvent { Username = username, AuthMethod = authMethod },
                (result, err) => new SftpAuthAfterEvent
                {
                    Username = username,
                    AuthMethod = authMethod,
                    WebSpaceUuid = result is not null && Guid.TryParse(result.Server, out var g) ? g : null,
                    Authenticated = result is not null && err is null,
                    Error = err,
                },
                () => AuthenticateCore(authMethod, username, password, publicKey));
        }
        catch (PluginHookCancelledException)
        {
            return null;
        }
    }

    private SftpAuthResult? AuthenticateCore(string authMethod, string username, string password, string? publicKey)
    {
        return WebSpaceAccessRoot.Resolve(_panel, _spaces, authMethod, username, password, publicKey, _logger);
    }

    private RootedSftpSession OpenSession(object channelOrTransport, SftpAuthResult auth, string? username)
    {
        Guid.TryParse(auth.Server, out var uuid);
        var user = username ?? auth.User ?? "";
        try
        {
            return _events.WithHooks(
                new SftpSessionOpenBeforeEvent { WebSpaceUuid = uuid, Username = user },
                (_, err) => new SftpSessionOpenAfterEvent
                {
                    WebSpaceUuid = uuid,
                    Username = user,
                    Error = err,
                },
                () => channelOrTransport switch
                {
                    SessionChannel ch => new RootedSftpSession(ch, auth.RootPath, auth.IsReadOnly, uuid, user, _events),
                    ISftpTransportChannel transport => new RootedSftpSession(transport, auth.RootPath, auth.IsReadOnly, uuid, user, _events),
                    _ => throw new InvalidOperationException("Unsupported SFTP channel type."),
                });
        }
        catch (PluginHookCancelledException)
        {
            throw;
        }
    }

    private sealed class PanelSftpAuthenticator : IAuthenticator
    {
        private readonly SftpHostedService _owner;

        public PanelSftpAuthenticator(SftpHostedService owner) => _owner = owner;

        public IEnumerable<string> SupportedMethods =>
            _owner._config.Sftp.DisablePasswordAuth
                ? ["publickey"]
                : ["password", "publickey"];

        public ValueTask<bool> IsPublicKeyAcceptableAsync(
            string username,
            string algorithm,
            ReadOnlyMemory<byte> publicKeyBlob,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<(AuthResult Result, AuthenticatedUser? User)> AuthenticateAsync(
            AuthContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.Equals(context.Method, "none", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));

                var sessionKey = context.SessionId is { Length: > 0 } id
                    ? Convert.ToHexString(id)
                    : "";

                string authMethod;
                string? publicKey = null;
                var password = "";
                var isPassword = false;

                if (string.Equals(context.Method, "publickey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.HasSignature)
                    {
                        // Signature-less publickey probe. The library turns
                        // Failure + IsPublicKeyAcceptableAsync() == true into a
                        // PK_OK reply, prompting the client to send the signed
                        // request. AuthResult.Continue has NO case in the
                        // library's dispatch switch — it falls through to the
                        // default branch, which sends USERAUTH_FAILURE, so the
                        // client never offers the signature and key-first
                        // clients (FileZilla, ssh -i) can never authenticate.
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
                    }

                    if (context.PublicKeyBlob is not { Length: > 0 } keyBlob)
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));

                    if (!_owner._attemptLimiter.AllowPublicKey(
                            sessionKey, _owner._config.Sftp.Limits.AuthenticationPubkeyAttempts))
                    {
                        _owner._logger?.Warning(LoggerTypes.Application,
                            $"SFTP ed25519 auth: user={context.Username} exceeded the publickey attempt limit; rejecting.");
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
                    }

                    authMethod = "public_key";
                    publicKey = Convert.ToBase64String(keyBlob);
                }
                else if (string.Equals(context.Method, "password", StringComparison.OrdinalIgnoreCase))
                {
                    if (_owner._config.Sftp.DisablePasswordAuth)
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));

                    if (!_owner._attemptLimiter.AllowPassword(
                            sessionKey, _owner._config.Sftp.Limits.AuthenticationPasswordAttempts))
                    {
                        _owner._logger?.Warning(LoggerTypes.Application,
                            $"SFTP ed25519 auth: user={context.Username} exceeded the password attempt limit; rejecting.");
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
                    }

                    isPassword = true;
                    authMethod = "password";
                    password = context.Password ?? "";
                }
                else
                {
                    return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
                }

                var result = _owner.Authenticate(authMethod, context.Username, password, publicKey);
                if (result is null)
                {
                    if (isPassword)
                        _owner._attemptLimiter.RecordPasswordFailure(sessionKey);
                    else
                        _owner._attemptLimiter.RecordPublicKeyFailure(sessionKey);
                    return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
                }

                if (sessionKey.Length > 0)
                    _owner._authBySession["conn:" + sessionKey] = result;
                var user = new AuthenticatedUser
                {
                    Username = context.Username,
                    Method = context.Method,
                    Properties = new Dictionary<string, object> { ["sftp_auth"] = result },
                };
                _owner._logger?.Info(LoggerTypes.Application,
                    $"SFTP auth ok user={context.Username} webspace={result.Server}");

                // Registration handshake with the connection handler: this authenticator
                // runs on the library's own dispatch thread, and the handler (on its
                // dedicated thread) completes the per-session signal once it is committed
                // to busy-spinning for the connection layer. Waiting here — BEFORE
                // returning Success — guarantees the handler's spin is already running
                // when the library constructs the connection layer and dispatches the
                // client's pipelined channel-open + "subsystem" request right after this
                // method returns. Without the handshake the handler's observation and
                // thread-scheduling latency can exceed the dispatch window and the hook
                // attaches too late ("subsystem request failed on channel 0").
                //
                // Bounded, best-effort: if the handler never registers in time (e.g. its
                // thread is starved), we proceed anyway after the budget — the auth reply
                // is never held hostage.
                if (sessionKey.Length > 0)
                {
                    var spinning = _owner._spinningSignals.GetOrAdd(sessionKey,
                        static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    var registered = false;
                    try
                    {
                        registered = spinning.Task.Wait(SftpHostedService.SpinningHandshakeBudget);
                    }
                    catch
                    {
                        // ignore — timeout or shutdown, best effort only
                    }
                    finally
                    {
                        _owner._spinningSignals.TryRemove(sessionKey, out _);
                    }

                    _owner._logger?.Debug(LoggerTypes.Application,
                        registered
                            ? $"SFTP ed25519 auth: handler was spinning before success was returned t={DateTime.UtcNow:HH:mm:ss.fff}"
                            : "SFTP ed25519 auth: handler registration TIMED OUT — proceeding best-effort (hook may land late)");
                }

                return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Success, user));
            }
            catch (Exception ex)
            {
                _owner._logger?.Warning(LoggerTypes.Application, $"SFTP ed25519 auth failed: {ex.Message}");
                return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
            }
        }
    }
}

public sealed class SftpAuthResult
{
    public string Server { get; set; } = "";
    public string User { get; set; } = "";
    public List<string> Permissions { get; set; } = [];

    [JsonIgnore]
    public string RootPath { get; set; } = "";

    /// <summary>Optional subdirectory jail relative to the WebSpace data root (from panel).</summary>
    [JsonPropertyName("root")]
    public string? RelativeRoot { get; set; }

    [JsonIgnore]
    public bool IsReadOnly =>
        Permissions.Count > 0
        && !Permissions.Any(p =>
            p.Contains("write", StringComparison.OrdinalIgnoreCase)
            || p.Contains("file.create", StringComparison.OrdinalIgnoreCase)
            || p.Contains("file.update", StringComparison.OrdinalIgnoreCase)
            || p is "*" or "admin" or "websocket.connect");
}
