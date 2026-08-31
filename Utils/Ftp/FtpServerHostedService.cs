using FubarDev.FtpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FeatherQuilld.Utils.Ftp;

/// <summary>Wraps <see cref="IFtpServerHost"/> as an <see cref="IHostedService"/>.</summary>
internal sealed class FtpServerHostedService : IHostedService
{
    private readonly IFtpServerHost _ftpServerHost;

    public FtpServerHostedService(IFtpServerHost ftpServerHost) => _ftpServerHost = ftpServerHost;

    public Task StartAsync(CancellationToken cancellationToken) =>
        _ftpServerHost.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _ftpServerHost.StopAsync(cancellationToken);
}
