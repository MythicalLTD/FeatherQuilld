using System.Collections.Concurrent;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// In-process brute-force protection for SFTP/FTP logins, keyed per username.
/// The daemon has no local auth attempt limiting of its own (only the HTTP
/// API has per-endpoint rate limiting), so a password guesser could otherwise
/// hammer SFTP/FTP logins bound only by the panel's own response latency.
///
/// This is intentionally simple/in-memory (no shared cache dependency): the
/// daemon is a single process per node, and the goal is to slow down/lock
/// out repeated failures against one account, not to be a distributed
/// rate limiter.
/// </summary>
internal static class SftpBruteForceGuard
{
    private const int MaxAttempts = 8;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);

    private sealed class State
    {
        public int Count;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private static readonly ConcurrentDictionary<string, State> Attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns remaining lockout time, or TimeSpan.Zero if not locked.</summary>
    public static TimeSpan GetLockoutRemaining(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return TimeSpan.Zero;

        if (!Attempts.TryGetValue(username, out var state))
            return TimeSpan.Zero;

        lock (state)
        {
            if (state.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
                return until - DateTimeOffset.UtcNow;
        }

        return TimeSpan.Zero;
    }

    public static void RecordFailure(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        var state = Attempts.GetOrAdd(username, static _ => new State { WindowStart = DateTimeOffset.UtcNow });
        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - state.WindowStart > AttemptWindow)
            {
                state.Count = 0;
                state.WindowStart = now;
            }

            state.Count++;
            if (state.Count >= MaxAttempts)
                state.LockedUntil = now + LockoutDuration;
        }
    }

    public static void Clear(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        Attempts.TryRemove(username, out _);
    }
}
