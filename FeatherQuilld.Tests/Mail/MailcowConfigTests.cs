using System.Net;
using System.Text;
using FeatherQuilld.Utils.Config.System;
using FeatherQuilld.Utils.Mail;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Mail;

/// <summary>
/// mailcow.conf handling. Upstream removed mailcow.conf.example, so the rules that
/// produce the file are tested directly instead of against the (gone) template.
/// </summary>
public class MailcowConfigTests
{
    private static MailConfig MakeMail(bool skipAcme = true, int httpPort = 8080, int httpsPort = 8443) =>
        new()
        {
            Enabled = true,
            Hostname = "mail.example.com",
            Backend = MailBackendKind.Mailcow,
            Mailcow = new MailcowConfig { HttpPort = httpPort, HttpsPort = httpsPort, SkipAcme = skipAcme },
        };

    private static IReadOnlyDictionary<string, string> MakeOverrides(MailConfig mail) =>
        MailcowConfigFile.BuildOverrides(mail, "mail.example.com", "Europe/Berlin", "writekey", "readkey", "127.0.0.1", false);

    [Fact]
    public void ApplyOverrides_ReplacesExistingAndAppendsMissing()
    {
        var lines = new List<string> { "MAILCOW_HOSTNAME=old.example.com", "# a comment", "DBNAME=mailcow" };

        var result = MailcowConfigFile.ApplyOverrides(lines, new Dictionary<string, string>
        {
            ["MAILCOW_HOSTNAME"] = "new.example.com",
            ["API_KEY"] = "abc",
        });

        Assert.Contains("MAILCOW_HOSTNAME=new.example.com", result);
        Assert.DoesNotContain("MAILCOW_HOSTNAME=old.example.com", result);
        Assert.Contains("API_KEY=abc", result);
        Assert.Equal(1, result.Count(l => l.StartsWith("MAILCOW_HOSTNAME=", StringComparison.Ordinal)));
        Assert.Contains("# a comment", result);
    }

    [Fact]
    public void ApplyOverrides_LeavesCommentsAndPrefixedKeysAlone()
    {
        var lines = new List<string> { "#HTTPS_PORT=443", "HTTP_PORT_EXTRA=1", "HTTPS_PORT=443" };

        var result = MailcowConfigFile.ApplyOverrides(lines, new Dictionary<string, string> { ["HTTPS_PORT"] = "8443" });

        Assert.Contains("#HTTPS_PORT=443", result);
        Assert.Contains("HTTP_PORT_EXTRA=1", result);
        Assert.Contains("HTTPS_PORT=8443", result);
    }

    [Fact]
    public void BuildOverrides_BindsLoopbackAndRegistersApiKey()
    {
        var overrides = MakeOverrides(MakeMail());

        Assert.Equal("mail.example.com", overrides["MAILCOW_HOSTNAME"]);
        Assert.Equal("8080", overrides["HTTP_PORT"]);
        Assert.Equal("8443", overrides["HTTPS_PORT"]);
        Assert.Equal("127.0.0.1", overrides["HTTP_BIND"]);
        Assert.Equal("127.0.0.1", overrides["HTTPS_BIND"]);
        Assert.Equal("y", overrides["SKIP_LETS_ENCRYPT"]);
        Assert.Equal("writekey", overrides["API_KEY"]);
        Assert.Equal("readkey", overrides["API_KEY_READ_ONLY"]);
        Assert.Equal("127.0.0.1", overrides["API_ALLOW_FROM"]);
        // the panel reaches the API through the published loopback port, where docker
        // rewrites the source IP; the key stays the guard.
        Assert.Equal("y", overrides["SKIP_IP_CHECK"]);
    }

    [Fact]
    public void BuildOverrides_KeepsMailcowAcmeWhenNotSkipped()
    {
        var overrides = MailcowConfigFile.BuildOverrides(MakeMail(skipAcme: false), "mail.example.com", "UTC",
            "w", "r", "127.0.0.1", false);

        Assert.Equal("n", overrides["SKIP_LETS_ENCRYPT"]);
    }

    [Fact]
    public void BuildFallbackConf_WritesEveryDocumentedKeyAndAppliesOverrides()
    {
        var lines = MailcowConfigFile.BuildFallbackConf("mail.example.com", "Europe/Berlin", MakeOverrides(MakeMail()),
            MailcowConfigFile.GenerateSecret);

        foreach (var key in MailcowConfigFile.FallbackKeys)
            Assert.Contains(lines, l => l.StartsWith(key + "=", StringComparison.Ordinal));

        Assert.Contains("MAILCOW_HOSTNAME=mail.example.com", lines);
        Assert.Contains("HTTPS_PORT=8443", lines);
        Assert.Contains("API_KEY=writekey", lines);
        Assert.Contains("SKIP_CLAMD=n", lines);
        Assert.Contains("TZ=Europe/Berlin", lines);
        Assert.Contains(lines, l => l.StartsWith("DBPASS=", StringComparison.Ordinal) && l.Length > "DBPASS=".Length);
        Assert.Contains(lines, l => l.StartsWith("REDISPASS=", StringComparison.Ordinal) && l.Length > "REDISPASS=".Length);
    }

    [Fact]
    public void BuildGenerateConfigCommand_IsNonInteractiveAndKeepsMailcowBranch()
    {
        var command = MailcowConfigFile.BuildGenerateConfigCommand("/opt/mailcow", "mail.example.com", "Europe/Berlin", true);

        Assert.Contains("cd /opt/mailcow", command);
        Assert.Contains("ln -sf mailcow.conf .env", command);
        Assert.Contains("MAILCOW_HOSTNAME=mail.example.com", command);
        Assert.Contains("MAILCOW_TZ=Europe/Berlin", command);
        Assert.Contains("SKIP_CLAMD=y", command);
        // an unanswered prompt would hang the install, so stdin is fed
        Assert.Contains("printf 'y", command);
        // --dev: no branch checkout, the clone already is on master
        Assert.Contains("--dev", command);
    }

    [Fact]
    public void GenerateApiKey_IsHexAndRandom()
    {
        var first = MailcowConfigFile.GenerateApiKey();
        var second = MailcowConfigFile.GenerateApiKey();

        Assert.Equal(48, first.Length);
        Assert.NotEqual(first, second);
        Assert.All(first, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void GenerateSecret_HasRequestedLengthAndShape()
    {
        var secret = MailcowConfigFile.GenerateSecret(28);

        Assert.Equal(28, secret.Length);
        Assert.All(secret, c => Assert.True(char.IsLetterOrDigit(c), $"unexpected character {c}"));
    }
}

/// <summary>mailcow API client against a fake HTTP handler; mailcow's own handler was removed.</summary>
public class MailcowApiClientDkimTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;

        public FakeHandler(string body) => _body = body;

        public string? LastPath { get; private set; }

        public string? LastBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (MailcowApiClient Client, FakeHandler Handler) MakeClient(string body)
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Enabled = true,
                    Hostname = "mail.example.com",
                    Backend = MailBackendKind.Mailcow,
                    Mailcow = new MailcowConfig { Url = "https://mail.example.com", ApiKey = "k" },
                },
            },
        };

        var handler = new FakeHandler(body);
        return (new MailcowApiClient(config, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task DeleteDomainAsync_SendsBareArrayBody()
    {
        // json_api.php assigns the whole delete request to $_POST['items'], so a wrapped
        // {"items": […]} deletes nothing and mailcow answers "access_denied".
        var (client, handler) = MakeClient("[]");

        await client.DeleteDomainAsync("example.com");

        Assert.Equal("[\"example.com\"]", handler.LastBody?.Trim());
        Assert.Equal("/api/v1/delete/domain", handler.LastPath);
    }

    [Fact]
    public async Task DeleteMailboxAsync_SendsBareArrayBody()
    {
        var (client, handler) = MakeClient("[]");

        await client.DeleteMailboxAsync("user@example.com");

        Assert.Equal("[\"user@example.com\"]", handler.LastBody?.Trim());
        Assert.Equal("/api/v1/delete/mailbox", handler.LastPath);
    }

    [Fact]
    public async Task AddDomainAsync_KeepsMailboxesUsable()
    {
        // mailcow treats mailboxes=0 as "no mailboxes at all" (count >= mailboxes), so a
        // freshly created domain must carry a real limit or every mailbox add fails.
        var (client, handler) = MakeClient("[{\"type\":\"success\",\"msg\":[\"domain added\"]}]");

        await client.AddDomainAsync("example.com");

        Assert.Contains("\"mailboxes\":\"10\"", handler.LastBody);
        Assert.Contains("\"active\":\"1\"", handler.LastBody);
    }

    [Fact]
    public async Task AddDomainAsync_UsesConfiguredMailboxLimit()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Backend = MailBackendKind.Mailcow,
                    Mailcow = new MailcowConfig { Url = "https://mail.example.com", ApiKey = "k", DomainMailboxLimit = 25 },
                },
            },
        };
        var handler = new FakeHandler("[]");
        using var client = new MailcowApiClient(config, new HttpClient(handler));

        await client.AddDomainAsync("example.com");

        Assert.Contains("\"mailboxes\":\"25\"", handler.LastBody);
    }

    [Fact]
    public async Task PingAsync_TrueOnVersionDocument()
    {
        var (client, handler) = MakeClient("{\"version\":\"2026-09a\"}");

        Assert.True(await client.PingAsync());
        Assert.Equal("/api/v1/get/status/version", handler.LastPath);
    }

    [Fact]
    public async Task PingAsync_FalseWithoutApiKey()
    {
        var config = new AppConfig
        {
            System = new SystemConfig
            {
                Mail = new MailConfig
                {
                    Backend = MailBackendKind.Mailcow,
                    Mailcow = new MailcowConfig { Url = "https://mail.example.com" },
                },
            },
        };

        using var client = new MailcowApiClient(config, new HttpClient(new FakeHandler("{\"version\":\"2026-09a\"}")));

        Assert.False(await client.PingAsync());
    }

    [Fact]
    public async Task PingAsync_FalseOnErrorBody()
    {
        var (client, _) = MakeClient("{\"type\":\"error\",\"msg\":\"api access denied\"}");

        Assert.False(await client.PingAsync());
    }

    [Fact]
    public async Task GetDkimAsync_ReadsSelectorAndTxtFromMailcowApi()
    {
        var (client, handler) = MakeClient("{\"dkim_selector\":\"dkim\",\"dkim_txt\":\"v=DKIM1;k=rsa;p=ABC\"}");

        var (selector, txt) = await client.GetDkimAsync("Example.com.");

        Assert.Equal("dkim", selector);
        Assert.Equal("v=DKIM1;k=rsa;p=ABC", txt);
        Assert.Equal("/api/v1/get/dkim/example.com", handler.LastPath);
    }

    [Fact]
    public async Task GetDkimAsync_JoinsSplitTxtChunks()
    {
        var (client, _) = MakeClient("{\"dkim_selector\":\"mail\",\"dkim_txt\":\"\\\"v=DKIM1;p=AAA\\\" \\\"BBB\\\"\"}");

        var (selector, txt) = await client.GetDkimAsync("example.com");

        Assert.Equal("mail", selector);
        Assert.Equal("v=DKIM1;p=AAABBB", txt);
    }

    [Fact]
    public async Task GetDkimAsync_ReturnsNullsForUnusableBody()
    {
        var (client, _) = MakeClient("[]");

        var (selector, txt) = await client.GetDkimAsync("example.com");

        Assert.Null(selector);
        Assert.Null(txt);
    }
}

/// <summary>DKIM hints with a mailcow backend must come from the API, not from files.</summary>
public class MailDnsHelperMailcowDkimTests
{
    private sealed class FakeApi : IMailcowApi
    {
        private readonly string? _selector;
        private readonly string? _txt;

        public FakeApi(string? selector, string? txt)
        {
            _selector = selector;
            _txt = txt;
        }

        public Task<(string? Selector, string? Txt)> GetDkimAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult((_selector, _txt));

        public Task<MailcowResult> AddDomainAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> DeleteDomainAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> AddMailboxAsync(string email, string password, bool active = true, long quotaMb = 0,
            CancellationToken ct = default) => Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> DeleteMailboxAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> EditMailboxAsync(string email, IReadOnlyDictionary<string, object?> attributes,
            CancellationToken ct = default) => Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> AddAliasAsync(string address, string destination, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> DeleteAliasAsync(string address, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> AddDkimAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<MailcowResult> SetSpamScoreAsync(string email, double score, CancellationToken ct = default) =>
            Task.FromResult(MailcowResult.Success());

        public Task<string?> GetContainersStatusAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("{}");
    }

    private static AppConfig MakeConfig(string backend) => new()
    {
        System = new SystemConfig
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "fq-mailcow-dkim-" + Guid.NewGuid().ToString("N")),
            Mail = new MailConfig
            {
                Enabled = true,
                Hostname = "mail.example.com",
                Backend = backend,
                DkimSelector = "mail",
            },
        },
    };

    [Fact]
    public void TryGetDkimRecord_UsesMailcowApiWhenBackendIsMailcow()
    {
        var config = MakeConfig(MailBackendKind.Mailcow);
        var api = new FakeApi("dkim", "v=DKIM1;k=rsa;p=FROMAPI");

        var record = MailDnsHelper.TryGetDkimRecord(config, "example.com", api);

        Assert.NotNull(record);
        Assert.Equal("dkim", record!.Value.Selector);
        Assert.Contains("FROMAPI", record.Value.Value);
        Assert.True(MailDnsHelper.IsDkimReady(config, "example.com", api));
        Assert.Contains(MailDnsHelper.BuildHints(config, "example.com", api),
            h => h.Type == "TXT" && h.Name == "dkim._domainkey");
    }

    [Fact]
    public void TryGetDkimRecord_IgnoresApiForDockerMailserverBackend()
    {
        var config = MakeConfig(MailBackendKind.DockerMailserver);
        var api = new FakeApi("dkim", "v=DKIM1;k=rsa;p=FROMAPI");

        // docker-mailserver keys are files; the mailcow client must not leak into that path.
        Assert.Null(MailDnsHelper.TryGetDkimRecord(config, "example.com", api));
    }

    [Fact]
    public void TryGetDkimRecord_FallsBackToFilesWhenApiHasNoKey()
    {
        var config = MakeConfig(MailBackendKind.Mailcow);
        var api = new FakeApi(null, null);

        Assert.Null(MailDnsHelper.TryGetDkimRecord(config, "example.com", api));

        var keyDir = Path.Combine(MailPaths.MailStateDir(config), "opendkim", "keys", "example.com");
        try
        {
            Directory.CreateDirectory(keyDir);
            File.WriteAllText(Path.Combine(keyDir, "mail.txt"), "v=DKIM1; k=rsa; p=FALLBACK");

            var record = MailDnsHelper.TryGetDkimRecord(config, "example.com", api);

            Assert.NotNull(record);
            Assert.Contains("FALLBACK", record!.Value.Value);
        }
        finally
        {
            try { Directory.Delete(config.System.RootDirectory, recursive: true); } catch { /* ignore */ }
        }
    }
}
