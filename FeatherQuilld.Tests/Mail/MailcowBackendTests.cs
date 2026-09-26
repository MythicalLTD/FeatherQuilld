using System.Net;
using System.Text;
using System.Text.Json;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

/// <summary>
/// mailcow backend coverage: API payloads, DKIM file handling, spam score mapping
/// and the autoresponder script. A fake API keeps the tests offline; the docker
/// based bits (sieve, stack detection) use a fake <c>docker</c> on PATH, mirroring
/// MailManagerDomainCommandTests.
/// </summary>
public class MailcowBackendTests
{
    internal sealed class FakeMailcowApi : IMailcowApi
    {
        public List<string> Calls { get; } = [];
        public List<IReadOnlyDictionary<string, object?>> Payloads { get; } = [];
        public string? FailWith { get; set; }

        private Task<MailcowResult> Record(string call, IReadOnlyDictionary<string, object?>? payload = null)
        {
            Calls.Add(call);
            if (payload is not null)
                Payloads.Add(payload);
            return Task.FromResult(FailWith is null
                ? MailcowResult.Success()
                : MailcowResult.Failure(FailWith));
        }

        public Task<MailcowResult> AddDomainAsync(string domain, CancellationToken ct = default) =>
            Record($"add/domain:{domain}");

        public Task<MailcowResult> DeleteDomainAsync(string domain, CancellationToken ct = default) =>
            Record($"delete/domain:{domain}");

        public Task<MailcowResult> AddMailboxAsync(string email, string password, bool active = true,
            long quotaMb = 0, CancellationToken ct = default) =>
            Record($"add/mailbox:{email}:{password}:{active}");

        public Task<MailcowResult> DeleteMailboxAsync(string email, CancellationToken ct = default) =>
            Record($"delete/mailbox:{email}");

        public Task<MailcowResult> EditMailboxAsync(string email,
            IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default)
        {
            Calls.Add($"edit/mailbox:{email}");
            Payloads.Add(attributes);
            return Task.FromResult(FailWith is null ? MailcowResult.Success() : MailcowResult.Failure(FailWith));
        }

        public Task<MailcowResult> AddAliasAsync(string address, string destination, CancellationToken ct = default) =>
            Record($"add/alias:{address}:{destination}");

        public Task<MailcowResult> DeleteAliasAsync(string address, CancellationToken ct = default) =>
            Record($"delete/alias:{address}");

        public Task<MailcowResult> AddDkimAsync(string domain, CancellationToken ct = default) =>
            Record($"add/dkim:{domain}");

        public Task<MailcowResult> SetSpamScoreAsync(string email, double score, CancellationToken ct = default) =>
            Record($"spam-score:{email}:{score}");

        public Task<string?> GetContainersStatusAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("[]");
    }

    private static AppConfig MakeConfig(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "fq-mailcow-" + Guid.NewGuid().ToString("N"));
        return new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = root,
                Timezone = "Europe/Berlin",
                Mail = new MailConfig
                {
                    Backend = MailBackendKind.Mailcow,
                    Hostname = "mail.example.com",
                    DataPath = Path.Combine(root, "mail"),
                    DkimSelector = "mail",
                    Mailcow = new MailcowConfig { HttpsPort = 8443 },
                },
            },
        };
    }

    [Fact]
    public void AddDomain_CallsApiAndRemembersDomainLocally()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api);

        backend.AddDomain("Example.COM");

        Assert.Contains("add/domain:example.com", api.Calls);
        Assert.Contains("add/dkim:example.com", api.Calls);
        Assert.Contains("example.com", backend.ListDomains());
    }

    [Fact]
    public void AddDomain_ToleratesAlreadyExistingDomain()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi { FailWith = "domain already exists" };
        var backend = new MailcowBackend(config, null, api);

        backend.AddDomain("example.com");

        Assert.Contains("example.com", backend.ListDomains());
    }

    [Fact]
    public void CreateMailbox_EnsuresDomainAndPassesPassword()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api);

        backend.CreateMailbox("user@example.com", "s3cret");

        Assert.Contains("add/domain:example.com", api.Calls);
        Assert.Contains("add/mailbox:user@example.com:s3cret:True", api.Calls);
    }

    [Fact]
    public void EnsureDkim_ReportsReadyWhenKeyFileExists()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api);

        var keyPath = MailcowPaths.DkimKeyFile(config, "example.com", "mail");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(keyPath, "v=DKIM1; k=rsa; p=abc123");

        Assert.True(backend.EnsureDkim("example.com", maxAttempts: 1, delayMs: 0));
        Assert.Contains("add/dkim:example.com", api.Calls);
    }

    [Fact]
    public void SetEnabledAndPassword_UseMailboxEditAttributes()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api);

        backend.UpdateMailboxPassword("user@example.com", "newpass");
        backend.SetMailboxEnabled("user@example.com", false);

        Assert.Contains("edit/mailbox:user@example.com", api.Calls);
        Assert.Equal("newpass", api.Payloads[0]["password"]);
        Assert.Equal("newpass", api.Payloads[0]["password2"]);
        Assert.Equal("0", api.Payloads[1]["active"]);
    }

    [Fact]
    public void Aliases_AreForwardedToTheApi()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api);

        backend.AddAlias("list@example.com", "member@example.com");
        backend.DeleteAlias("list@example.com", "member@example.com");

        Assert.Contains("add/alias:list@example.com:member@example.com", api.Calls);
        Assert.Contains("delete/alias:list@example.com", api.Calls);
    }

    [Fact]
    public void SpamFilterOff_SetsBypassScoreAndRemembersState()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api);

        Assert.True(backend.GetSpamFilterEnabled("user@example.com"));

        backend.SetSpamFilterEnabled("user@example.com", false);

        Assert.Contains($"spam-score:user@example.com:{MailcowBackend.SpamBypassScore}", api.Calls);
        Assert.False(backend.GetSpamFilterEnabled("user@example.com"));

        backend.SetSpamFilterEnabled("user@example.com", true);
        Assert.Contains("spam-score:user@example.com:0", api.Calls);
        Assert.True(backend.GetSpamFilterEnabled("user@example.com"));
    }

    [Fact]
    public void AutorespondScript_EscapesSubjectAndBody()
    {
        var script = MailcowSieve.BuildAutorespondScript("Hi \"there\"", "line1\nline2\\end", "user@example.com");

        Assert.Contains("require [\"vacation\", \"variables\"];", script);
        Assert.Contains("vacation", script);
        Assert.Contains(":subject \"Hi \\\"there\\\"\"", script);
        Assert.Contains("line1 line2\\\\end", script);
        Assert.Contains(":addresses [\"user@example.com\"]", script);
        Assert.DoesNotContain("\nline1", script);
    }

    [Fact]
    public void ManagerWithMailcowBackend_ProvisionsMailboxThroughApi()
    {
        var config = MakeConfig();
        var api = new FakeMailcowApi();
        var backend = new MailcowBackend(config, null, api) { };

        // The manager guards on IsRunning(); a fake docker with the mailcow labels
        // answers the container lookup so the guard passes without a live stack.
        using var docker = new FakeDockerOnPath(config);
        var manager = new MailManager(config, null, backend);

        var result = manager.Provision(new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["email"] = "user@example.com",
            ["password"] = "pw",
        });

        Assert.NotNull(result);
        Assert.Equal(MailBackendKind.Mailcow, manager.BackendKind);
        Assert.Contains("add/mailbox:user@example.com:pw:True", api.Calls);
        Assert.Contains("add/domain:example.com", api.Calls);
    }

    /// <summary>Fake <c>docker</c> on PATH that reports a running mailcow stack.</summary>
    internal sealed class FakeDockerOnPath : IDisposable
    {
        private readonly string _dir;
        private readonly string? _originalPath;

        public string InvocationLog { get; }

        public FakeDockerOnPath(AppConfig config)
        {
            _dir = Path.Combine(config.System.RootDirectory!, "bin");
            Directory.CreateDirectory(_dir);
            InvocationLog = Path.Combine(_dir, "invocations.log");

            var script = Path.Combine(_dir, "docker");
            File.WriteAllText(script, $"""
                #!/usr/bin/env bash
                echo "$@" >> "{InvocationLog}"
                if [[ "$1" == "ps" ]]; then
                  echo "mailcowdockerized-dovecot-mailcow-1"
                fi
                exit 0
                """);
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + _originalPath);
        }

        public IReadOnlyList<string[]> Invocations() =>
            File.Exists(InvocationLog)
                ? File.ReadAllLines(InvocationLog).Where(l => l.Length > 0).Select(l => l.Split(' ')).ToList()
                : [];

        public void Dispose()
        {
            if (_originalPath is not null)
                Environment.SetEnvironmentVariable("PATH", _originalPath);
        }
    }
}

/// <summary>Payload/URL building of the mailcow REST client, asserted through a fake HTTP handler.</summary>
public class MailcowApiClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body, string? ApiKey)> Requests { get; } = [];
        public string ResponseBody { get; set; } = """[{"type":"success","log":[],"msg":["mailbox added"]}]""";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>Optional per-request responder (used for GET lookups); falls back to <see cref="ResponseBody"/>.</summary>
        public Func<HttpRequestMessage, string?>? Responder { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body,
                request.Headers.TryGetValues("X-API-Key", out var key) ? string.Join(",", key) : null));
            var response = Responder?.Invoke(request) ?? ResponseBody;
            return new HttpResponseMessage(Status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static (MailcowApiClient Client, CapturingHandler Handler) MakeClient(string? url = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-mailcow-api-" + Guid.NewGuid().ToString("N"));
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = root,
                Mail = new MailConfig
                {
                    Backend = MailBackendKind.Mailcow,
                    Hostname = "mail.example.com",
                    Mailcow = new MailcowConfig { Url = url ?? "https://mail.example.com", ApiKey = "test-key", HttpsPort = 8443 },
                },
            },
        };

        var handler = new CapturingHandler();
        var client = new MailcowApiClient(config, new HttpClient(handler) { BaseAddress = new Uri(url ?? "https://mail.example.com") });
        return (client, handler);
    }

    [Fact]
    public async Task AddMailbox_PostsLocalPartDomainAndPassword()
    {
        var (client, handler) = MakeClient();

        var result = await client.AddMailboxAsync("user@example.com", "pw123");

        Assert.True(result.Ok);
        var request = handler.Requests.Single();
        Assert.Equal("/api/v1/add/mailbox", request.Path);
        Assert.Equal("test-key", request.ApiKey);

        using var doc = JsonDocument.Parse(request.Body!);
        Assert.Equal("user", doc.RootElement.GetProperty("local_part").GetString());
        Assert.Equal("example.com", doc.RootElement.GetProperty("domain").GetString());
        Assert.Equal("pw123", doc.RootElement.GetProperty("password").GetString());
        Assert.Equal("1", doc.RootElement.GetProperty("active").GetString());
    }

    [Fact]
    public async Task SpamScore_UsesMailboxSpecificEndpoint()
    {
        var (client, handler) = MakeClient();

        await client.SetSpamScoreAsync("user@example.com", 9999);

        var request = handler.Requests.Single();
        Assert.Equal("/api/v1/edit/spam-score/", request.Path);
        using var doc = JsonDocument.Parse(request.Body!);
        Assert.Equal("user@example.com", doc.RootElement.GetProperty("items")[0].GetString());
        Assert.Equal(9999, doc.RootElement.GetProperty("attr").GetProperty("spam_score").GetDouble());
    }

    [Fact]
    public async Task EditMailbox_WrapsItemsAndAttributes()
    {
        var (client, handler) = MakeClient();

        await client.EditMailboxAsync("user@example.com", new Dictionary<string, object?> { ["active"] = "0" });

        Assert.Equal("/api/v1/edit/mailbox", handler.Requests.Single().Path);
        using var doc = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.Equal("user@example.com", doc.RootElement.GetProperty("items")[0].GetString());
        Assert.Equal("0", doc.RootElement.GetProperty("attr").GetProperty("active").GetString());
    }

    [Fact]
    public void Interpret_FlagsNonSuccessTypesAsFailure()
    {
        Assert.True(MailcowApiClient.Interpret("""[{"type":"success","msg":["ok"]}]""").Ok);

        var failure = MailcowApiClient.Interpret("""[{"type":"danger","msg":["domain exists"]}]""");
        Assert.False(failure.Ok);
        Assert.Contains("domain exists", failure.Raw);
    }

    [Fact]
    public void ResolveBaseUrl_DerivesFromHostnameAndPort()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Hostname = "mail.example.com",
                    Mailcow = new MailcowConfig { HttpsPort = 8443 },
                },
            },
        };

        Assert.Equal("https://mail.example.com:8443/", MailcowApiClient.ResolveBaseUrl(config).ToString());

        config.System.Mail.Mailcow.Url = "mail.example.com";
        Assert.Equal("https://mail.example.com/", MailcowApiClient.ResolveBaseUrl(config).ToString());
    }

    [Fact]
    public async Task AddDomain_PostsAnActiveDomain()
    {
        var (client, handler) = MakeClient();

        await client.AddDomainAsync("example.com");

        var request = handler.Requests.Single();
        Assert.Equal("/api/v1/add/domain", request.Path);
        using var doc = JsonDocument.Parse(request.Body!);
        Assert.Equal("example.com", doc.RootElement.GetProperty("domain").GetString());
        // An inactive domain makes mailcow reject mail, so it must be "1".
        Assert.Equal("1", doc.RootElement.GetProperty("active").GetString());
        Assert.Equal("0", doc.RootElement.GetProperty("relay_all_recipients").GetString());
    }

    [Fact]
    public async Task DeleteAlias_ResolvesAliasIdFirst()
    {
        var (client, handler) = MakeClient();
        handler.Responder = request => request.Method == HttpMethod.Get
            ? """[{"id":6,"address":"list@example.com","goto":"a@example.com"}]"""
            : """[{"type":"success","msg":["alias_removed"]}]""";

        var result = await client.DeleteAliasAsync("list@example.com");

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Contains("/api/v1/get/alias/", handler.Requests[0].Path);

        var deleteRequest = handler.Requests[1];
        Assert.Equal("/api/v1/delete/alias", deleteRequest.Path);
        using var doc = JsonDocument.Parse(deleteRequest.Body!);
        Assert.Equal("6", doc.RootElement.GetProperty("items")[0].GetString());
    }

    [Fact]
    public async Task DeleteAlias_IsNoOpWhenAliasIsGone()
    {
        var (client, handler) = MakeClient();
        handler.Responder = request => request.Method == HttpMethod.Get ? "[]" : """[{"type":"success"}]""";

        var result = await client.DeleteAliasAsync("gone@example.com");

        Assert.True(result.Ok);
        // Both lookups (by address, then the full list) are GETs; nothing is deleted.
        Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("/delete/alias"));
    }

    [Theory]
    [InlineData("""[{"id":6,"address":"list@example.com"}]""", "list@example.com", "6")]
    [InlineData("""[{"id":"9","address":"other@example.com"}]""", "other@example.com", "9")]
    [InlineData("""[{"id":6,"address":"other@example.com"}]""", "list@example.com", null)]
    [InlineData("{}", "list@example.com", null)]
    [InlineData("", "list@example.com", null)]
    public void FindIdForAddress_MatchesOnlyTheRequestedAddress(string json, string address, string? expected)
    {
        Assert.Equal(expected, MailcowApiClient.FindIdForAddress(json, address));
    }

    [Fact]
    public void ResolveApiKey_PrefersConfigAndFallsBackToFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-mailcow-key-" + Guid.NewGuid().ToString("N"));
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                RootDirectory = root,
                Mail = new MailConfig { Backend = MailBackendKind.Mailcow, DataPath = Path.Combine(root, "mail") },
            },
        };

        // no config key, no file yet
        Assert.Equal("", MailcowApiClient.ResolveApiKey(config));

        var keyPath = MailcowPaths.ApiKeyFile(config);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(keyPath, "file-key\n");
        Assert.Equal("file-key", MailcowApiClient.ResolveApiKey(config));

        config.System.Mail.Mailcow.ApiKey = "config-key";
        Assert.Equal("config-key", MailcowApiClient.ResolveApiKey(config));
    }
}
