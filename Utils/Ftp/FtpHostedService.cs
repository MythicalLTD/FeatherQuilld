using FeatherQuilld.Utils.Config.Ftp;
using FeatherQuilld.Utils.Logger;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Utils.Ftp;

/// <summary>No-op hosted service when FTP is disabled; logs status when enabled via <see cref="FtpServiceCollectionExtensions"/>.</summary>
public sealed class FtpHostedService : IHostedService
{
    private readonly AppConfig _config;
    private readonly AppLogger? _logger;

    public FtpHostedService(AppConfig config, AppLogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Ftp.Enabled)
        {
            _logger?.Info(LoggerTypes.Application, "FTP disabled");
            return Task.CompletedTask;
        }

        _logger?.Info(LoggerTypes.Application,
            $"FTP listening on 0.0.0.0:{_config.Ftp.Port} pasv={_config.Ftp.PassivePortMin}-{_config.Ftp.PassivePortMax}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
