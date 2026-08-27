using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace FeatherQuilld.Utils.Logger;

/// <summary>
/// Forwards <see cref="Microsoft.Extensions.Logging.ILogger"/> into <see cref="Logger"/>.
/// </summary>
public sealed class MicrosoftLoggerProvider(Logger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new Adapter(logger, categoryName);

    public void Dispose()
    {
    }

    private sealed class Adapter(Logger logger, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MsLogLevel logLevel) =>
            logLevel is not MsLogLevel.None;

        public void Log<TState>(
            MsLogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
                return;

            var type = MapType(category);
            var level = MapLevel(logLevel);

            if (exception is not null)
            {
                var text = string.IsNullOrEmpty(message) ? exception.Message : message;
                // Cancellation during soft shutdown is expected — never escalate to ERROR.
                if (exception is OperationCanceledException or TaskCanceledException &&
                    level < LoggerLevel.Error)
                {
                    logger.Log(type, level, text);
                    return;
                }

                if (level >= LoggerLevel.Error)
                    logger.Error(type, text, exception);
                else
                    logger.Log(type, level, $"{text}: {exception.GetType().Name}: {exception.Message}");
                return;
            }

            logger.Log(type, level, message);
        }

        private static LoggerTypes MapType(string category)
        {
            if (category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                category.StartsWith("Microsoft.Hosting", StringComparison.Ordinal))
                return LoggerTypes.WebServer;

            return LoggerTypes.Application;
        }

        private static LoggerLevel MapLevel(MsLogLevel level) => level switch
        {
            MsLogLevel.Trace or MsLogLevel.Debug => LoggerLevel.Debug,
            MsLogLevel.Information => LoggerLevel.Info,
            MsLogLevel.Warning => LoggerLevel.Warning,
            _ => LoggerLevel.Error
        };
    }
}
