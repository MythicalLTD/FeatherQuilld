using System.Diagnostics;
using System.Text.RegularExpressions;
using FeatherQuilld.Utils.Logger;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;

namespace FeatherQuilld.Middleware;

/// <summary>
/// Logs each HTTP request and its response when the app logger has debug enabled.
/// </summary>
public sealed partial class RequestResponseLoggingMiddleware(RequestDelegate next, AppLogger logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var req = context.Request;
        var path = req.Path.HasValue ? req.Path.Value! : "/";
        var query = req.QueryString.HasValue ? MaskSecrets(req.QueryString.Value!) : null;
        var target = query is null ? path : path + query;
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "-";

        logger.Debug(LoggerTypes.WebServer, $"← {req.Method} {target} from {remote}");

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            var status = context.Response.StatusCode;
            logger.Debug(
                LoggerTypes.WebServer,
                $"→ {req.Method} {path} {status} {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// Masks bearer/upload token values in a query string before logging so the
    /// node bearer never lands in request logs, proxy logs or shell history.
    /// The rest of the query is left untouched.
    /// </summary>
    internal static string MaskSecrets(string queryString) =>
        TokenQueryRegex().Replace(queryString, "${prefix}***");

    [GeneratedRegex(@"(?<prefix>(?:^|[?&])token=)[^&]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenQueryRegex();
}
