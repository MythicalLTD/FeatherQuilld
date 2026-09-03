using FeatherQuilld.Commands;
using FeatherQuilld.Utils.Remote;

namespace FeatherQuilld.Tests.Remote;

public class ConfigureOAuthBuildRequestTests
{
    [Fact]
    public void BuildRequest_BehindProxy_UsesHttpsScheme()
    {
        var request = ConfigureOAuth.BuildRequest(
            new ConfigureOAuthOptions
            {
                NodeName = "web-1",
                NodeFqdn = "node.example.com",
                BehindProxy = true,
            },
            nodeIp: "203.0.113.10",
            locationId: 7,
            panelUrl: "http://panel.example.com");

        Assert.Equal("https", request.Scheme);
        Assert.True(request.BehindProxy);
        Assert.Equal("node.example.com", request.Fqdn);
        Assert.Equal(7, request.LocationId);
    }

    [Fact]
    public void BuildRequest_HttpPanel_UsesHttpScheme()
    {
        var request = ConfigureOAuth.BuildRequest(
            new ConfigureOAuthOptions
            {
                NodeName = "web-1",
                NodeFqdn = "203.0.113.10",
            },
            nodeIp: "203.0.113.10",
            locationId: 3,
            panelUrl: "http://212.87.213.118:8721");

        Assert.Equal("http", request.Scheme);
        Assert.False(request.BehindProxy);
    }

    [Fact]
    public void BuildRequest_HttpsPanel_UsesHttpsScheme()
    {
        var request = ConfigureOAuth.BuildRequest(
            new ConfigureOAuthOptions
            {
                NodeName = "web-1",
                NodeFqdn = "node.example.com",
            },
            nodeIp: "203.0.113.10",
            locationId: 3,
            panelUrl: "https://panel.example.com");

        Assert.Equal("https", request.Scheme);
    }
}

public class CreateLocationRequestTests
{
    [Fact]
    public void Serializes_WingsCompatible_WebLocationPayload()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new CreateLocationRequest
        {
            Name = "EU West",
            Type = "web",
            Description = "Amsterdam",
            FlagCode = "nl",
        });

        Assert.Contains("\"name\":\"EU West\"", json);
        Assert.Contains("\"type\":\"web\"", json);
        Assert.Contains("\"description\":\"Amsterdam\"", json);
        Assert.Contains("\"flag_code\":\"nl\"", json);
    }
}
