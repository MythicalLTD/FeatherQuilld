using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FeatherQuilld.Utils.Remote;

namespace FeatherQuilld.Tests.Remote;

public class DeviceAuthClientTests
{
    [Fact]
    public void ParseEnvelope_ReadsErrorAndErrorCode()
    {
        var envelope = DeviceAuthClient.ParseEnvelope("""
            {
              "success": false,
              "message": "still waiting",
              "error": "authorization_pending",
              "error_code": "authorization_pending"
            }
            """);

        Assert.False(envelope.Success);
        Assert.Equal("authorization_pending", DeviceAuthClient.ResolveErrorCode(envelope));
        Assert.Equal("still waiting", envelope.Message);
    }

    [Fact]
    public void ParseEnvelope_IgnoresBooleanErrorFalseOnSuccess()
    {
        // Real FeatherPanel success envelope: "error": false (bool), not a string.
        var envelope = DeviceAuthClient.ParseEnvelope("""
            {
              "success": true,
              "message": "OAuth2 device authorization started",
              "data": { "device_code": "fpdev_x", "user_code": "ABCD-EFGH" },
              "error": false,
              "error_message": null,
              "error_code": null
            }
            """);

        Assert.True(envelope.Success);
        Assert.Null(envelope.Error);
        Assert.Null(DeviceAuthClient.ResolveErrorCode(envelope));
        Assert.Equal("OAuth2 device authorization started", envelope.Message);
    }

    [Fact]
    public void ParseEnvelope_PrefersErrorCodeOverError()
    {
        var envelope = DeviceAuthClient.ParseEnvelope("""
            {
              "success": false,
              "error": "authorization_pending",
              "error_code": "slow_down"
            }
            """);

        Assert.Equal("slow_down", DeviceAuthClient.ResolveErrorCode(envelope));
    }

    [Fact]
    public async Task StartAsync_PostsDeviceGrantWithoutCallbackUrl()
    {
        string? postedBody = null;
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://panel.example.com/api/user/api-clients/oauth2/device",
                request.RequestUri!.ToString());
            postedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "success": true,
                  "data": {
                    "device_code": "fpdev_secret",
                    "user_code": "ABCD-EFGH",
                    "verification_uri": "https://panel.example.com/dashboard/account/oauth2/api/device",
                    "verification_uri_complete": "https://panel.example.com/dashboard/account/oauth2/api/device?user_code=ABCD-EFGH",
                    "expires_in": 900,
                    "interval": 5
                  }
                }
                """);
        });

        using var http = new HttpClient(handler);
        using var client = new DeviceAuthClient("https://panel.example.com/", http: http);

        var grant = await client.StartAsync(new DeviceAuthorizationRequest
        {
            Name = "FeatherQuilld on node",
            Description = "test",
            AppName = "FeatherQuilld",
            AllowedIps = "",
            AlertCors = "false",
        });

        Assert.Equal("fpdev_secret", grant.DeviceCode);
        Assert.Equal("ABCD-EFGH", grant.UserCode);
        Assert.Equal(900, grant.ExpiresIn);
        Assert.Equal(5, grant.Interval);
        Assert.Contains("ABCD-EFGH", grant.VerificationUriComplete);
        Assert.NotNull(postedBody);
        Assert.DoesNotContain("callbackurl", postedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\":\"FeatherQuilld on node\"", postedBody);
        Assert.Contains("\"appName\":\"FeatherQuilld\"", postedBody);
    }

    [Fact]
    public async Task PollForCredentialsAsync_WaitsThroughPendingThenReturnsKeys()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "success": false,
                      "error_code": "authorization_pending",
                      "message": "pending"
                    }
                    """));
            }

            return Task.FromResult(JsonResponse("""
                {
                  "success": true,
                  "data": {
                    "token_type": "featherpanel_api_key",
                    "public_key": "pub_test",
                    "private_key": "priv_test",
                    "authorization_code": null,
                    "issued_at": "2026-01-01T00:00:00Z"
                  }
                }
                """));
        });

        using var http = new HttpClient(handler);
        using var client = new DeviceAuthClient("https://panel.example.com", http: http);

        var credentials = await client.PollForCredentialsAsync(
            "fpdev_secret",
            intervalSeconds: 1,
            expiresInSeconds: 30);

        Assert.Equal(2, calls);
        Assert.Equal("pub_test", credentials.PublicKey);
        Assert.Equal("priv_test", credentials.PrivateKey);
        Assert.Equal("featherpanel_api_key", credentials.TokenType);
    }

    [Fact]
    public async Task PollForCredentialsAsync_SlowDownIncreasesInterval()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(JsonResponse("""
                    {
                      "success": false,
                      "error": "slow_down",
                      "data": { "interval": 2 }
                    }
                    """));
            }

            return Task.FromResult(JsonResponse("""
                {
                  "success": true,
                  "data": {
                    "public_key": "pub",
                    "private_key": "priv"
                  }
                }
                """));
        });

        using var http = new HttpClient(handler);
        using var client = new DeviceAuthClient("https://panel.example.com", http: http);

        var statuses = new List<DeviceAuthPollStatus>();
        var progress = new Progress<DeviceAuthPollStatus>(statuses.Add);

        var credentials = await client.PollForCredentialsAsync(
            "fpdev_secret",
            intervalSeconds: 1,
            expiresInSeconds: 30,
            progress);

        Assert.Equal("pub", credentials.PublicKey);
        Assert.Contains(statuses, s => s.Phase == DeviceAuthPollPhase.SlowDown && s.IntervalSeconds == 2);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    [InlineData("INVALID_DEVICE_CODE")]
    public async Task PollForCredentialsAsync_TerminalErrorsStop(string errorCode)
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(JsonResponse($$"""
                {
                  "success": false,
                  "error_code": "{{errorCode}}",
                  "message": "stop"
                }
                """)));

        using var http = new HttpClient(handler);
        using var client = new DeviceAuthClient("https://panel.example.com", http: http);

        var ex = await Assert.ThrowsAsync<DeviceAuthException>(() =>
            client.PollForCredentialsAsync("fpdev_secret", intervalSeconds: 1, expiresInSeconds: 30));

        Assert.Equal(
            errorCode is "INVALID_DEVICE_CODE" ? "INVALID_DEVICE_CODE" : errorCode,
            ex.ErrorCode);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
