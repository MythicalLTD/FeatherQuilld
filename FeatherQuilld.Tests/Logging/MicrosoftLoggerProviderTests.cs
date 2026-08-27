using FeatherQuilld.Utils.Logger;
using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Tests.Logging;

public sealed class MicrosoftLoggerProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLogger _appLogger;
    private readonly MicrosoftLoggerProvider _provider;

    public MicrosoftLoggerProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fq-log-" + Guid.NewGuid().ToString("N"));
        _appLogger = new AppLogger(new LoggerOptions
        {
            Directory = _dir,
            Debug = true,
            MaxArchives = 0,
        });
        _provider = new MicrosoftLoggerProvider(_appLogger);
    }

    [Fact]
    public void WarningWithException_DoesNotEscalateToError()
    {
        var ms = _provider.CreateLogger("Test.Category");
        ms.Log(MsLogLevel.Warning, new EventId(1), "Failed to sync schedules", new InvalidOperationException("boom"),
            (s, _) => s!);

        var text = File.ReadAllText(Path.Combine(_dir, "latest.log"));
        Assert.Contains("[WARNING]", text);
        Assert.DoesNotContain("[ERROR]", text);
    }

    [Fact]
    public void WarningWithCancellation_DoesNotDumpStackAsError()
    {
        var ms = _provider.CreateLogger("Test.Category");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ms.Log(MsLogLevel.Warning, new EventId(1), "Failed to sync schedules",
            new OperationCanceledException(cts.Token), (s, _) => s!);

        var text = File.ReadAllText(Path.Combine(_dir, "latest.log"));
        Assert.Contains("[WARNING]", text);
        Assert.DoesNotContain("[ERROR]", text);
        Assert.DoesNotContain("at FeatherQuilld", text);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _appLogger.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
