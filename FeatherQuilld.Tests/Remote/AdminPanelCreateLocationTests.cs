using System.Net;
using System.Text;
using FeatherQuilld.Utils.Remote;

namespace FeatherQuilld.Tests.Remote;

public class AdminPanelCreateLocationTests
{
    [Fact]
    public async Task CreateWebLocationAsync_PostsTypeWeb_AndReturnsLocation()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/admin/locations", request.RequestUri!.AbsolutePath);
            capturedBody = await request.Content!.ReadAsStringAsync(ct);

            var payload = """
                {
                  "success": true,
                  "data": {
                    "location": {
                      "id": 42,
                      "name": "Web EU",
                      "type": "web",
                      "flag_code": "de",
                      "description": "Frankfurt"
                    }
                  }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        });

        using var http = new HttpClient(handler);
        using var client = new AdminPanelClient("https://panel.example.com", "fp_test", http: http);

        var location = await client.CreateWebLocationAsync(new CreateLocationRequest
        {
            Name = "Web EU",
            Description = "Frankfurt",
            FlagCode = "DE",
        });

        Assert.Equal(42, location.Id);
        Assert.Equal("Web EU", location.Name);
        Assert.Equal("web", location.Type);
        Assert.Equal("de", location.FlagCode);
        Assert.NotNull(capturedBody);
        Assert.Contains("\"type\":\"web\"", capturedBody);
        Assert.Contains("\"flag_code\":\"de\"", capturedBody);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
