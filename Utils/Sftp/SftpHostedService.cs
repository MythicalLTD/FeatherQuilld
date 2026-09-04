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
    private readonly ConcurrentDictionary<uint, byte> _sftpChannels = new();
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
            MaxAuthAttempts = Math.Max(1, _config.Sftp.Limits.AuthenticationPasswordAttempts),
        };
        options.HostKeys.Add(hostKey);

        _embeddedServer = new EmbeddedSshServer(options, new IPEndPoint(IPAddress.Any, _config.Sftp.Port));
        _embeddedCts = new CancellationTokenSource();
        var ct = _embeddedCts.Token;

        _embeddedServer.ConnectionAccepted += connection =>
        {
            _ = Task.Run(() => HandleEmbeddedConnectionAsync(connection, ct), ct);
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
            HookSubsystemRequests(connection);

            // Bind pending username auth onto this connection once authenticated.
            if (connection.User is { Username: { Length: > 0 } username }
                && _authBySession.TryRemove("user:" + username, out var auth))
            {
                _authBySession["conn:" + Convert.ToHexString(connection.SessionId.ToArray())] = auth;
            }

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
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleEmbeddedChannelAsync(connection, channel, ct), ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(LoggerTypes.Application, $"SFTP ed25519 connection ended: {ex.Message}");
        }
        finally
        {
            try
            {
                _authBySession.TryRemove("conn:" + Convert.ToHexString(connection.SessionId.ToArray()), out _);
            }
            catch { /* ignore */ }
        }
    }

    private void HookSubsystemRequests(SshConnection connection)
    {
        try
        {
            var field = typeof(SshConnection).GetField(
                "_connectionLayer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(connection) is not ConnectionLayer layer)
                return;

            layer.ChannelRequestReceived += (channel, request, _) =>
            {
                if (!string.Equals(request.RequestType, "subsystem", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(false);

                var name = ReadSshString(request.RequestData.Span);
                if (!string.Equals(name, "sftp", StringComparison.OrdinalIgnoreCase))
                    return ValueTask.FromResult(false);

                _sftpChannels[channel.LocalChannelId] = 1;
                if (channel.Environment is not null)
                    channel.Environment["featherquilld.subsystem"] = "sftp";
                return ValueTask.FromResult(true);
            };
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"SFTP subsystem hook failed: {ex.Message}");
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
                if (IsSftpChannel(channel))
                    break;
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            if (!IsSftpChannel(channel))
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
            _ = OpenSession(transport, auth, auth.User);
            _logger?.Debug(LoggerTypes.Application, $"SFTP subsystem attached root={auth.RootPath}");

            while (!ct.IsCancellationRequested && !channel.IsClosed)
                await Task.Delay(250, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Application, $"SFTP ed25519 channel failed: {ex.Message}");
            try { await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
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

        var username = connection.User?.Username;
        if (!string.IsNullOrEmpty(username)
            && _authBySession.TryGetValue("user:" + username, out auth!))
        {
            return true;
        }

        return false;
    }

    private bool IsSftpChannel(SshChannel channel)
    {
        if (_sftpChannels.ContainsKey(channel.LocalChannelId))
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
            _logger?.Warning(LoggerTypes.Application, $"Failed to attach rooted SFTP: {ex.Message}");
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

                string authMethod;
                string? publicKey = null;
                var password = "";

                if (string.Equals(context.Method, "publickey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.HasSignature)
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Continue, null));

                    if (context.PublicKeyBlob is not { Length: > 0 } keyBlob)
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));

                    authMethod = "public_key";
                    publicKey = Convert.ToBase64String(keyBlob);
                }
                else if (string.Equals(context.Method, "password", StringComparison.OrdinalIgnoreCase))
                {
                    if (_owner._config.Sftp.DisablePasswordAuth)
                        return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));

                    authMethod = "password";
                    password = context.Password ?? "";
                }
                else
                {
                    return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));
                }

                var result = _owner.Authenticate(authMethod, context.Username, password, publicKey);
                if (result is null)
                    return ValueTask.FromResult<(AuthResult, AuthenticatedUser?)>((AuthResult.Failure, null));

                _owner._authBySession["user:" + context.Username] = result;
                var user = new AuthenticatedUser
                {
                    Username = context.Username,
                    Method = context.Method,
                    Properties = new Dictionary<string, object> { ["sftp_auth"] = result },
                };
                _owner._logger?.Info(LoggerTypes.Application,
                    $"SFTP auth ok user={context.Username} webspace={result.Server}");
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
