using FxSsh.Services;

namespace FeatherQuilld.Utils.Sftp;

internal sealed class FxSshTransportChannel : ISftpTransportChannel
{
    private readonly SessionChannel _channel;

    public FxSshTransportChannel(SessionChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _channel.DataReceived += OnData;
        _channel.CloseReceived += OnClosed;
        _channel.EofReceived += OnClosed;
    }

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler? Closed;

    public void SendData(byte[] data) => _channel.SendData(data);

    private void OnData(object? sender, byte[] data) => DataReceived?.Invoke(this, data);

    private void OnClosed(object? sender, EventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}
