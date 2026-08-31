using System.Net;
using System.Net.Sockets;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.DotNet;
using FeatherQuilld.Utils.Config.Ftp;
using FeatherQuilld.Utils.Remote;
using FeatherQuilld.Utils.WebSpaces;
using Microsoft.Extensions.DependencyInjection;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Ftp;

public static class FtpServiceCollectionExtensions
{
    public static void AddFeatherQuilldFtp(
        this IServiceCollection services,
        AppConfig config,
        AppLogger? logger = null)
    {
        if (!config.Ftp.Enabled)
            return;

        services.AddSingleton(config.Ftp);
        services.AddFtpServer(builder => builder.UseDotNetFileSystem());
        services.AddSingleton<IMembershipProvider>(sp =>
            new PanelFtpMembershipProvider(
                sp.GetRequiredService<FtpConfig>(),
                sp.GetRequiredService<WebSpaceStore>(),
                sp.GetRequiredService<IPanelClient>(),
                logger));
        services.AddSingleton<IAccountDirectoryQuery, PanelFtpAccountDirectoryQuery>();
        services.AddSingleton<IFileSystemClassFactory, PanelFtpFileSystemFactory>();

        services.Configure<FtpServerOptions>(opt =>
        {
            opt.ServerAddress = "0.0.0.0";
            opt.Port = config.Ftp.Port;
        });

        var passiveHost = string.IsNullOrWhiteSpace(config.Ftp.PassiveHost)
            ? null
            : config.Ftp.PassiveHost.Trim();
        services.Configure<SimplePasvOptions>(opt =>
        {
            var min = (ushort)Math.Clamp(config.Ftp.PassivePortMin, 1024, 65535);
            var max = (ushort)Math.Clamp(config.Ftp.PassivePortMax, min, 65535);
            opt.PasvMinPort = min;
            opt.PasvMaxPort = max;
            if (!string.IsNullOrWhiteSpace(passiveHost))
            {
                if (IPAddress.TryParse(passiveHost, out var address))
                {
                    opt.PublicAddress = address;
                }
                else
                {
                    try
                    {
                        var addresses = global::System.Net.Dns.GetHostAddresses(passiveHost);
                        opt.PublicAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                            ?? addresses.FirstOrDefault();
                    }
                    catch
                    {
                        // Best-effort; clients may need PASV host set manually.
                    }
                }
            }
        });

        services.Configure<DotNetFileSystemOptions>(opt => opt.RootPath = string.Empty);
        services.AddHostedService<FtpServerHostedService>();
    }
}
