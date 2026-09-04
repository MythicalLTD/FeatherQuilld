using Microsoft.AspNetCore.Cors.Infrastructure;

namespace FeatherQuilld.Utils.Startup;

/// <summary>Builds the daemon default CORS policy from <c>api.allowed_origins</c>.</summary>
public static class CorsPolicyConfigurator
{
    public static void Apply(CorsPolicyBuilder policy, IEnumerable<string>? allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var origins = (allowedOrigins ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (origins.Length == 0)
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    }
}
