using System.Security.Cryptography;
using d0x2a.EmbeddedSsh.Auth;
using d0x2a.EmbeddedSsh.Connection;
using d0x2a.EmbeddedSsh.Crypto;
using d0x2a.EmbeddedSsh.HostKeys;
using d0x2a.EmbeddedSsh.Protocol;
using d0x2a.EmbeddedSsh.Protocol.Messages;
using d0x2a.EmbeddedSsh.Transport;
using d0x2a.EmbeddedSsh.Transport.Algorithms;

namespace d0x2a.EmbeddedSsh;

/// <summary>
/// Represents a single SSH connection.
/// </summary>
public sealed class SshConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SshServerOptions _options;
    private readonly TransportLayer _transport;
    private readonly CancellationTokenSource _cts = new();

    private ConnectionState _state = ConnectionState.AwaitingVersion;
    private byte[]? _sessionId;
    private AuthenticatedUser? _authenticatedUser;
    private ConnectionLayer? _connectionLayer;
    private ChannelManager? _channelManager;

    private byte[]? _clientKexInit;
    private byte[]? _serverKexInit;
    private KexInitMessage? _clientKexInitMessage;

    /// <summary>
    /// Gets the connection state.
    /// </summary>
    public ConnectionState State => _state;

    /// <summary>
    /// Gets the authenticated user, if any.
    /// </summary>
    public AuthenticatedUser? User => _authenticatedUser;

    /// <summary>
    /// Gets the session identifier.
    /// </summary>
    public ReadOnlySpan<byte> SessionId => _sessionId;

    /// <summary>
    /// Gets whether the connection is authenticated.
    /// </summary>
    public bool IsAuthenticated => _authenticatedUser != null;

    /// <summary>
    /// Gets the channel manager for this connection.
    /// </summary>
    public ChannelManager? Channels => _channelManager;

    public SshConnection(Stream stream, SshServerOptions options)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transport = new TransportLayer(stream);
    }

    /// <summary>
    /// Runs the connection state machine.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var ct = linkedCts.Token;

        try
        {
            // Version exchange
            await PerformVersionExchangeAsync(ct).ConfigureAwait(false);

            // Key exchange
            await PerformKeyExchangeAsync(ct).ConfigureAwait(false);

            // Service request (should be "ssh-userauth")
            await HandleServiceRequestAsync(ct).ConfigureAwait(false);

            // Authentication
            await PerformAuthenticationAsync(ct).ConfigureAwait(false);

            // Connection layer — pass ChannelRequestHandler at construction so
            // subsystem (and other non-builtin) requests are handled without a
            // post-auth race against ChannelRequestReceived subscribers.
            _channelManager = new ChannelManager();
            _connectionLayer = new ConnectionLayer(
                _transport, _channelManager, _options.ChannelRequestHandler);

            _state = ConnectionState.Connected;

            // Process messages until disconnect
            await ProcessMessagesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (SshProtocolException ex)
        {
            // Send disconnect message
            try
            {
                var disconnect = new DisconnectMessage
                {
                    Reason = ex.Reason,
                    Description = ex.Message,
                    LanguageTag = ""
                };
                await _transport.SendMessageAsync(disconnect, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors sending disconnect
            }
            throw;
        }
        finally
        {
            _state = ConnectionState.Disconnected;
        }
    }

    private async Task PerformVersionExchangeAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.HandshakeTimeout);

        var clientVersion = await _transport.ExchangeVersionsAsync(_options.ServerVersion, timeoutCts.Token)
            .ConfigureAwait(false);

        _state = ConnectionState.AwaitingKexInit;
    }

    private async Task PerformKeyExchangeAsync(CancellationToken ct)
    {
        // Wait for client KEXINIT - use raw bytes for exchange hash
        var clientKexInitPayload = await _transport.ReceivePacketAsync(ct).ConfigureAwait(false);
        var clientKexInitMsg = MessageParser.Parse(clientKexInitPayload);
        if (clientKexInitMsg is not KexInitMessage clientKexInit)
        {
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Expected KEXINIT, got {clientKexInitMsg.MessageType}");
        }

        _clientKexInitMessage = clientKexInit;
        _clientKexInit = clientKexInitPayload; // Use raw bytes, not reconstructed

        // Select algorithms
        var (kexAlg, hostKeyAlg, cipherC2S, cipherS2C) = NegotiateAlgorithms(clientKexInit);

        if (hostKeyAlg != "ssh-ed25519")
            throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, $"Unsupported host key: {hostKeyAlg}");

        var supportedCiphers = new[] { "aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com" };
        if (!supportedCiphers.Contains(cipherC2S) || !supportedCiphers.Contains(cipherS2C))
            throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, "Unsupported cipher");

        // Instantiate the negotiated key exchange algorithm
        IKexAlgorithm kex = kexAlg switch
        {
            "mlkem768x25519-sha256" => new MlKem768x25519Kex(),
            "curve25519-sha256" or "curve25519-sha256@libssh.org" => new Curve25519Kex(),
            _ => throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, $"Unsupported KEX: {kexAlg}")
        };

        // Send our KEXINIT
        var serverKexInit = CreateKexInit();
        _serverKexInit = serverKexInit.ToBytes();
        await _transport.SendMessageAsync(serverKexInit, ct).ConfigureAwait(false);

        _state = ConnectionState.KexInProgress;

        // Get host key
        var hostKey = _options.GetHostKey(hostKeyAlg)
            ?? throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, "No host key available");

        // Wait for KEX init (message 30 — used by both ECDH and hybrid KEX)
        var kexEcdhMsg = await _transport.ReceiveMessageAsync(ct).ConfigureAwait(false);
        if (kexEcdhMsg is not KexEcdhInitMessage kexEcdhInit)
        {
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Expected KEX_ECDH_INIT, got {kexEcdhMsg.MessageType}");
        }

        // Perform key exchange
        var result = kex.ServerExchange(kexEcdhInit.ClientPublicKey.Span);

        // Compute exchange hash
        var hostKeyBlob = hostKey.GetPublicKeyBlob();
        var exchangeHash = kex.ComputeExchangeHash(
            _transport.ClientVersion,
            _transport.ServerVersion,
            _clientKexInit,
            _serverKexInit,
            hostKeyBlob,
            kexEcdhInit.ClientPublicKey.Span,
            result.ServerEphemeral,
            result.SharedSecret);

        // First exchange hash becomes session ID
        _sessionId = exchangeHash;

        // Sign exchange hash
        var signature = hostKey.Sign(exchangeHash);

        // Send KEX reply (message 31 — used by both ECDH and hybrid KEX)
        var kexReply = new KexEcdhReplyMessage
        {
            HostKeyBlob = hostKeyBlob,
            ServerPublicKey = result.ServerEphemeral,
            Signature = signature
        };
        await _transport.SendMessageAsync(kexReply, ct).ConfigureAwait(false);

        // Send NEWKEYS
        await _transport.SendMessageAsync(NewKeysMessage.Instance, ct).ConfigureAwait(false);

        // Wait for client NEWKEYS
        var newKeysMsg = await _transport.ReceiveMessageAsync(ct).ConfigureAwait(false);
        if (newKeysMsg is not NewKeysMessage)
        {
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Expected NEWKEYS, got {newKeysMsg.MessageType}");
        }

        // Derive keys based on negotiated cipher
        var secretEncoding = kex.SharedSecretEncoding;
        ISshCipher sendCipher;
        ISshCipher receiveCipher;

        if (cipherS2C == "aes256-gcm@openssh.com")
        {
            var (c2sKeys, s2cKeys) = KeyDerivation.DeriveAllKeys(
                result.SharedSecret, exchangeHash, _sessionId,
                ivSize: 12,   // AES-GCM uses 12-byte IV
                keySize: 32,  // 256-bit key
                integrityKeySize: 0,  // AEAD, no separate MAC key
                encoding: secretEncoding);

            var send = new AesGcmCipher();
            send.Initialize(s2cKeys.EncryptionKey, s2cKeys.Iv);
            sendCipher = send;

            var receive = new AesGcmCipher();
            receive.Initialize(c2sKeys.EncryptionKey, c2sKeys.Iv);
            receiveCipher = receive;
        }
        else // chacha20-poly1305@openssh.com
        {
            var (c2sKeys, s2cKeys) = KeyDerivation.DeriveAllKeys(
                result.SharedSecret, exchangeHash, _sessionId,
                ivSize: 0,    // ChaCha20-Poly1305 doesn't use IV
                keySize: 64,  // 64 bytes (two 32-byte keys)
                integrityKeySize: 0,  // AEAD, no separate MAC key
                encoding: secretEncoding);

            var send = new ChaCha20Poly1305Cipher();
            send.Initialize(s2cKeys.EncryptionKey, ReadOnlySpan<byte>.Empty);
            sendCipher = send;

            var receive = new ChaCha20Poly1305Cipher();
            receive.Initialize(c2sKeys.EncryptionKey, ReadOnlySpan<byte>.Empty);
            receiveCipher = receive;
        }

        // Activate encryption
        _transport.ActivateKeys(sendCipher, receiveCipher, exchangeHash);

        // Send EXT_INFO with server-sig-algs to advertise supported signature algorithms (RFC 8308)
        var extInfo = new ExtInfoMessage
        {
            Extensions = [("server-sig-algs", "rsa-sha2-256,rsa-sha2-512,ssh-ed25519")]
        };
        await _transport.SendMessageAsync(extInfo, ct).ConfigureAwait(false);

        _state = ConnectionState.AwaitingNewKeys;
    }

    private async Task HandleServiceRequestAsync(CancellationToken ct)
    {
        ISshMessage serviceMsg;

        // The client may send EXT_INFO before SERVICE_REQUEST (RFC 8308)
        // Skip any EXT_INFO messages
        while (true)
        {
            serviceMsg = await _transport.ReceiveMessageAsync(ct).ConfigureAwait(false);
            if (serviceMsg is ExtInfoMessage)
                continue; // Ignore client's EXT_INFO
            break;
        }

        if (serviceMsg is not ServiceRequestMessage serviceRequest)
        {
            throw new SshProtocolException(DisconnectReason.ProtocolError,
                $"Expected SERVICE_REQUEST, got {serviceMsg.MessageType}");
        }

        if (serviceRequest.ServiceName != "ssh-userauth")
        {
            throw new SshProtocolException(DisconnectReason.ServiceNotAvailable,
                $"Service {serviceRequest.ServiceName} not available");
        }

        var accept = new ServiceAcceptMessage { ServiceName = "ssh-userauth" };
        await _transport.SendMessageAsync(accept, ct).ConfigureAwait(false);

        _state = ConnectionState.Authenticating;
    }

    private async Task PerformAuthenticationAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.AuthTimeout);

        // Send banner if configured
        if (!string.IsNullOrEmpty(_options.Banner))
        {
            var banner = new UserauthBannerMessage
            {
                Message = _options.Banner,
                LanguageTag = ""
            };
            await _transport.SendMessageAsync(banner, timeoutCts.Token).ConfigureAwait(false);
        }

        var authLayer = new AuthLayer(_transport, _options.Authenticator!, _options.MaxAuthAttempts);
        _authenticatedUser = await authLayer.AuthenticateAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task ProcessMessagesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await _transport.ReceiveMessageAsync(ct).ConfigureAwait(false);

            switch (message)
            {
                case DisconnectMessage:
                    return;

                case IgnoreMessage:
                case DebugMessage:
                    // Ignore these messages
                    break;

                case ServiceRequestMessage service when service.ServiceName == "ssh-connection":
                    var accept = new ServiceAcceptMessage { ServiceName = "ssh-connection" };
                    await _transport.SendMessageAsync(accept, ct).ConfigureAwait(false);
                    break;

                default:
                    // Delegate to connection layer
                    if (_connectionLayer != null)
                    {
                        await _connectionLayer.ProcessMessageAsync(message, ct).ConfigureAwait(false);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Accepts a new channel.
    /// </summary>
    public async ValueTask<SshChannel> AcceptChannelAsync(CancellationToken cancellationToken = default)
    {
        if (_connectionLayer == null)
            throw new InvalidOperationException("Connection not authenticated");

        return await _connectionLayer.AcceptChannelAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string kex, string hostKey, string cipherC2S, string cipherS2C) NegotiateAlgorithms(KexInitMessage clientKexInit)
    {
        // Our supported algorithms (in preference order)
        var serverKex = MLKem.IsSupported
            ? new[] { "mlkem768x25519-sha256", "curve25519-sha256", "curve25519-sha256@libssh.org" }
            : new[] { "curve25519-sha256", "curve25519-sha256@libssh.org" };
        var serverHostKey = new[] { "ssh-ed25519" };
        var serverCipher = new[] { "aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com" };

        var kex = Negotiate(clientKexInit.KexAlgorithms, serverKex)
            ?? throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, "No common KEX algorithm");

        var hostKey = Negotiate(clientKexInit.HostKeyAlgorithms, serverHostKey)
            ?? throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, "No common host key algorithm");

        var cipherC2S = Negotiate(clientKexInit.EncryptionAlgorithmsClientToServer, serverCipher)
            ?? throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, "No common cipher (c2s)");

        var cipherS2C = Negotiate(clientKexInit.EncryptionAlgorithmsServerToClient, serverCipher)
            ?? throw new SshProtocolException(DisconnectReason.KeyExchangeFailed, "No common cipher (s2c)");

        return (kex, hostKey, cipherC2S, cipherS2C);
    }

    private static string? Negotiate(IReadOnlyList<string> client, string[] server)
    {
        foreach (var c in client)
        {
            if (server.Contains(c))
                return c;
        }
        return null;
    }

    private static KexInitMessage CreateKexInit()
    {
        var kexAlgorithms = MLKem.IsSupported
            ? new List<string> { "mlkem768x25519-sha256", "curve25519-sha256", "curve25519-sha256@libssh.org", "ext-info-s" }
            : new List<string> { "curve25519-sha256", "curve25519-sha256@libssh.org", "ext-info-s" };

        return new KexInitMessage
        {
            Cookie = RandomNumberGenerator.GetBytes(16),
            KexAlgorithms = kexAlgorithms,
            HostKeyAlgorithms = ["ssh-ed25519"],
            EncryptionAlgorithmsClientToServer = ["aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com"],
            EncryptionAlgorithmsServerToClient = ["aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com"],
            MacAlgorithmsClientToServer = ["hmac-sha2-256"],  // Not used with AEAD
            MacAlgorithmsServerToClient = ["hmac-sha2-256"],  // Not used with AEAD
            CompressionAlgorithmsClientToServer = ["none"],
            CompressionAlgorithmsServerToClient = ["none"],
            LanguagesClientToServer = [],
            LanguagesServerToClient = [],
            FirstKexPacketFollows = false
        };
    }

    /// <summary>
    /// Disconnects the connection.
    /// </summary>
    public async ValueTask DisconnectAsync(DisconnectReason reason = DisconnectReason.ByApplication, string message = "")
    {
        try
        {
            var disconnect = new DisconnectMessage
            {
                Reason = reason,
                Description = message,
                LanguageTag = ""
            };
            await _transport.SendMessageAsync(disconnect, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors
        }

        await _cts.CancelAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>
/// Connection state.
/// </summary>
public enum ConnectionState
{
    /// <summary>Waiting for client version string.</summary>
    AwaitingVersion,

    /// <summary>Waiting for client KEXINIT.</summary>
    AwaitingKexInit,

    /// <summary>Key exchange in progress.</summary>
    KexInProgress,

    /// <summary>Waiting for NEWKEYS.</summary>
    AwaitingNewKeys,

    /// <summary>Waiting for service request.</summary>
    AwaitingServiceRequest,

    /// <summary>Authentication in progress.</summary>
    Authenticating,

    /// <summary>Authenticated and connected.</summary>
    Connected,

    /// <summary>Connection closed.</summary>
    Disconnected
}
