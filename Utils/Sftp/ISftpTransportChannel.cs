namespace FeatherQuilld.Utils.Sftp;

/// <summary>Minimal byte channel used by <see cref="RootedSftpSession"/>.</summary>
public interface ISftpTransportChannel
{
    event EventHandler<byte[]>? DataReceived;
    event EventHandler? Closed;
    void SendData(byte[] data);
}
