using System.Collections.Concurrent;

namespace FeatherQuilld.Utils.Ftp;

internal sealed record FtpSessionContext(string RootPath, bool ReadOnly);

internal static class FtpSessionStore
{
    private static readonly ConcurrentDictionary<string, FtpSessionContext> Sessions = new(StringComparer.Ordinal);

    public static void Set(string username, FtpSessionContext context) =>
        Sessions[username] = context;

    public static bool TryGet(string username, out FtpSessionContext context) =>
        Sessions.TryGetValue(username, out context!);

    public static void Remove(string username) => Sessions.TryRemove(username, out _);
}
