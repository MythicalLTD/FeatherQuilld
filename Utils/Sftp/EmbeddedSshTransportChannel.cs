using d0x2a.EmbeddedSsh.Connection;

namespace FeatherQuilld.Utils.Sftp;

/// <summary>Adapts an EmbeddedSsh channel to <see cref="ISftpTransportChannel"/>.</summary>
internal sealed class EmbeddedSshTransportChannel : ISftpTransportChannel, IAsyncDisposable
{
    private readonly SshChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private int _closed;

    public EmbeddedSshTransportChannel(SshChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _pump = Task.Run(PumpAsync);
    }

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? Closed;

    public void SendData(byte[] data)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;
        try
        {
            _channel.WriteAsync(data, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            RaiseClosed();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && !_channel.IsClosed)
            {
                var chunk = await _channel.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (chunk.Length == 0)
                {
                    if (_channel.EofReceived || _channel.IsClosed)
                        break;
                    continue;
                }

                DataReceived?.Invoke(this, chunk.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // shut down
        }
        catch
        {
            // channel closed
        }
        finally
        {
            RaiseClosed();
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        try { _cts.Cancel(); } catch { /* ignore */ }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        RaiseClosed();
        try { await _pump.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
