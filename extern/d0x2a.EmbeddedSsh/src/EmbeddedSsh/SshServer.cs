using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace d0x2a.EmbeddedSsh;

/// <summary>
/// SSH server that accepts incoming connections.
/// </summary>
public sealed class SshServer : IAsyncDisposable
{
    private readonly SshServerOptions _options;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, SshConnection> _connections = new();
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private bool _isRunning;

    /// <summary>
    /// Event raised when a new connection is established.
    /// </summary>
    public event Func<SshConnection, Task>? ConnectionAccepted;

    /// <summary>
    /// Event raised when a connection is closed.
    /// </summary>
    public event Action<SshConnection, Exception?>? ConnectionClosed;

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    public int ActiveConnections => _connections.Count;

    /// <summary>
    /// Gets whether the server is running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Creates a new SSH server.
    /// </summary>
    /// <param name="options">Server options.</param>
    /// <param name="endpoint">Endpoint to listen on.</param>
    public SshServer(SshServerOptions options, IPEndPoint endpoint)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();

        _listener = new TcpListener(endpoint);
        _connectionSemaphore = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    /// <summary>
    /// Creates a new SSH server on the specified port.
    /// </summary>
    public SshServer(SshServerOptions options, int port = 22)
        : this(options, new IPEndPoint(IPAddress.Any, port))
    {
    }

    /// <summary>
    /// Starts the server.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            throw new InvalidOperationException("Server is already running");

        _listener.Start();
        _isRunning = true;
        _acceptTask = AcceptConnectionsAsync();
    }

    /// <summary>
    /// Stops the server.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // Close all connections
        foreach (var connection in _connections.Values)
        {
            try
            {
                await connection.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors
            }
        }

        _connections.Clear();
    }

    private async Task AcceptConnectionsAsync()
    {
        var ct = _cts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for connection slot
                await _connectionSemaphore.WaitAsync(ct).ConfigureAwait(false);

                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    _connectionSemaphore.Release();
                    throw;
                }

                // Handle connection in background
                _ = HandleConnectionAsync(client);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (!_isRunning)
            {
                // Listener was stopped
                break;
            }
            catch
            {
                // Log error but continue accepting
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        var connectionId = Guid.NewGuid();
        SshConnection? connection = null;
        Exception? error = null;

        try
        {
            var stream = client.GetStream();
            connection = new SshConnection(stream, _options);
            _connections[connectionId] = connection;

            // Notify about new connection
            if (ConnectionAccepted != null)
            {
                await ConnectionAccepted(connection).ConfigureAwait(false);
            }

            // Run the connection
            await connection.RunAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            _connectionSemaphore.Release();

            if (connection != null)
            {
                ConnectionClosed?.Invoke(connection, error);
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _connectionSemaphore.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Extension methods for SshServer.
/// </summary>
public static class SshServerExtensions
{
    /// <summary>
    /// Runs the server and processes channels with a handler.
    /// </summary>
    public static async Task RunWithHandlerAsync(
        this SshServer server,
        Func<SshConnection, Connection.SshChannel, CancellationToken, Task> channelHandler,
        CancellationToken cancellationToken = default)
    {
        server.ConnectionAccepted += async connection =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && connection.State == ConnectionState.Connected)
                {
                    var channel = await connection.AcceptChannelAsync(cancellationToken).ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await channelHandler(connection, channel, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            await channel.DisposeAsync().ConfigureAwait(false);
                        }
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal
            }
            catch (Exception)
            {
                // Connection error
            }
        };

        server.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        await server.StopAsync().ConfigureAwait(false);
    }
}
