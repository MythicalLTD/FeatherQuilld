using System.Collections.Concurrent;

namespace FeatherQuilld.Utils.WebSpaces;

/// <summary>
/// Tracks live console permission overrides and revocation cutoffs for WebSpace users.
/// Panel calls deauthorize / push-permissions after subuser CRUD.
/// </summary>
public sealed class WebSpaceUserAccessService
{
    private readonly ConcurrentDictionary<string, AccessState> _states = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(Guid userUuid, Guid webspaceUuid) =>
        $"{userUuid:D}:{webspaceUuid:D}";

    public void Deauthorize(Guid userUuid, IEnumerable<Guid> webspaceUuids)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var ws in webspaceUuids)
        {
            var key = Key(userUuid, ws);
            _states.AddOrUpdate(
                key,
                _ => new AccessState(now, now, Array.Empty<string>(), Revoked: true),
                (_, existing) => existing with
                {
                    RevokedAt = now,
                    PermissionsUpdatedAt = now,
                    Revoked = true,
                    Permissions = Array.Empty<string>(),
                });
        }
    }

    public void SetPermissions(Guid userUuid, Guid webspaceUuid, IReadOnlyList<string> permissions)
    {
        var now = DateTimeOffset.UtcNow;
        var key = Key(userUuid, webspaceUuid);
        _states.AddOrUpdate(
            key,
            _ => new AccessState(null, now, permissions.ToArray(), Revoked: false),
            (_, existing) => existing with
            {
                Permissions = permissions.ToArray(),
                PermissionsUpdatedAt = now,
                Revoked = false,
                RevokedAt = null,
            });
    }

    public bool IsJwtRevoked(Guid userUuid, Guid webspaceUuid, long jwtIatUnix)
    {
        if (!_states.TryGetValue(Key(userUuid, webspaceUuid), out var state))
            return false;

        if (state.Revoked && state.RevokedAt is not null && jwtIatUnix <= state.RevokedAt.Value.ToUnixTimeSeconds())
            return true;

        // Permission push also invalidates older tokens so clients re-auth with new claims.
        if (state.PermissionsUpdatedAt is not null
            && jwtIatUnix < state.PermissionsUpdatedAt.Value.ToUnixTimeSeconds())
            return true;

        return false;
    }

    public IReadOnlyList<string>? GetLivePermissions(Guid userUuid, Guid webspaceUuid)
    {
        if (!_states.TryGetValue(Key(userUuid, webspaceUuid), out var state) || state.Revoked)
            return null;

        return state.Permissions;
    }

    private sealed record AccessState(
        DateTimeOffset? RevokedAt,
        DateTimeOffset? PermissionsUpdatedAt,
        IReadOnlyList<string> Permissions,
        bool Revoked);
}
