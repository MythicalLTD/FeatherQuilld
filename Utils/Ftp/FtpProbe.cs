using System.Net.Sockets;
using FeatherQuilld.Utils.Config.Ftp;

namespace FeatherQuilld.Utils.Ftp;

public static class FtpProbe
{
    public static bool IsListening(FtpConfig config)
    {
        if (!config.Enabled || config.Port <= 0)
            return false;

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", config.Port);
            if (!connect.Wait(TimeSpan.FromSeconds(2)))
                return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
