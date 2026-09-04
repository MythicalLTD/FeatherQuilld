using FeatherQuilld.Utils.Startup;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace FeatherQuilld.Tests.Startup;

public class CorsPolicyConfiguratorTests
{
    [Fact]
    public void Apply_EmptyOrigins_AllowAnyOriginWithoutCredentials()
    {
        var policy = Build([]);
        Assert.True(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public void Apply_WhitespaceOrigins_TreatedAsEmpty()
    {
        var policy = Build(["", "  ", "\t"]);
        Assert.True(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public void Apply_ExplicitOrigins_AllowsCredentials()
    {
        var policy = Build(["https://panel.example"]);
        Assert.False(policy.AllowAnyOrigin);
        Assert.True(policy.SupportsCredentials);
        Assert.Contains("https://panel.example", policy.Origins);
    }

    private static CorsPolicy Build(IEnumerable<string> origins)
    {
        var builder = new CorsPolicyBuilder();
        CorsPolicyConfigurator.Apply(builder, origins);
        return builder.Build();
    }
}
