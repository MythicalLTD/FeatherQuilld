using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FeatherQuilld.Plugins.Events;
using Certes;
using Certes.Acme;
using AuthStatus = Certes.Acme.Resource.AuthorizationStatus;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;
using FeatherQuilld.Utils.Remote;
using IoDirectory = System.IO.Directory;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Issues/renews Let's Encrypt certificates for nginx via Certes HTTP-01 or DNS-01 (wildcard).
/// Challenge files: <c>{RootDirectory}/acme/www/.well-known/acme-challenge/</c>
/// Certs: <c>/etc/featherquilld/certs/{domain}.crt|.key</c>
/// </summary>
public sealed class NginxAcmeService
{
    public const string DefaultCertDirectory = "/etc/featherquilld/certs";

    private readonly AppConfig _config;
    private readonly AppLogger? _logger;
    private readonly IEventBus _events;
    private readonly IPanelClient? _panel;
    private readonly object _gate = new();

    public NginxAcmeService(
        AppConfig config,
        AppLogger? logger = null,
        IEventBus? events = null,
        IPanelClient? panel = null)
    {
        _config = config;
        _logger = logger;
        _events = events.OrNoOp();
        _panel = panel;
    }

    public string AcmeWwwRoot => Path.Combine(_config.System.RootDirectory, "acme", "www");
    public string AcmeAccountDir => Path.Combine(_config.System.RootDirectory, "acme");
    public string ChallengeRoot => Path.Combine(AcmeWwwRoot, ".well-known", "acme-challenge");

    public static string CertPath(string domain) => Path.Combine(DefaultCertDirectory, $"{domain}.crt");
    public static string KeyPath(string domain) => Path.Combine(DefaultCertDirectory, $"{domain}.key");

    /// <summary>True when cert exists and is valid for more than 30 days.</summary>
    public static bool IsCertFresh(string domain, int minDaysRemaining = 30)
    {
        var crt = CertPath(domain);
        if (!File.Exists(crt) || !File.Exists(KeyPath(domain)))
            return false;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(crt);
            return cert.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(minDaysRemaining);
        }
        catch
        {
            return false;
        }
    }

    public void EnsureChallengeLayout()
    {
        IoDirectory.CreateDirectory(ChallengeRoot);
        IoDirectory.CreateDirectory(DefaultCertDirectory);
        IoDirectory.CreateDirectory(AcmeAccountDir);
    }

    /// <summary>Issue or skip certs for SSL domains. Best-effort; logs failures.</summary>
    public Task EnsureCertsAsync(
        IEnumerable<string> domains,
        CancellationToken cancellationToken = default,
        bool force = false,
        string? email = null)
    {
        var list = domains as IReadOnlyList<string> ?? domains.ToList();
        return _events.WithHooksAsync(
            new AcmeEnsureCertsBeforeEvent { Domains = list },
            err => new AcmeEnsureCertsAfterEvent { Domains = list, Error = err },
            token => EnsureCertsCoreAsync(list, token, force, email),
            cancellationToken);
    }

    /// <summary>
    /// Issue <c>apex</c> + <c>*.apex</c> via DNS-01 (panel writes PowerDNS TXT on the linked zone).
    /// Cert files are written as <c>{apex}.crt|.key</c>.
    /// </summary>
    public Task EnsureWildcardCertAsync(
        Guid webspaceUuid,
        string apex,
        string? email = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        apex = apex.Trim().TrimEnd('.').ToLowerInvariant();
        return _events.WithHooksAsync(
            new AcmeEnsureCertsBeforeEvent { Domains = [apex, "*." + apex] },
            err => new AcmeEnsureCertsAfterEvent { Domains = [apex, "*." + apex], Error = err },
            token => EnsureWildcardCoreAsync(webspaceUuid, apex, email, force, token),
            cancellationToken);
    }

    internal static string AccountFileName(string email, bool staging)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return staging ? $"{hash}-staging.pem" : $"{hash}.pem";
    }

    private async Task EnsureCertsCoreAsync(
        IEnumerable<string> domains,
        CancellationToken cancellationToken,
        bool force,
        string? email)
    {
        var resolved = (email ?? _config.System.Proxy.AcmeEmail)?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            _logger?.Debug(LoggerTypes.Proxy, "nginx ACME skipped no acme_email");
            return;
        }

        EnsureChallengeLayout();
        IoDirectory.CreateDirectory(Path.Combine(AcmeAccountDir, "accounts"));
        var needed = domains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant())
            .Distinct()
            .Where(d => force || !IsCertFresh(d))
            .ToList();

        if (needed.Count == 0)
            return;

        if (force)
        {
            foreach (var domain in needed)
            {
                try
                {
                    if (File.Exists(CertPath(domain))) File.Delete(CertPath(domain));
                    if (File.Exists(KeyPath(domain))) File.Delete(KeyPath(domain));
                }
                catch
                {
                    // ignore stale delete failures
                }
            }
        }

        lock (_gate)
        {
            // serialize ACME account usage
        }

        try
        {
            await IssueAsync(resolved, needed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Proxy, $"nginx ACME failed: {ex.Message}");
        }
    }

    private async Task EnsureWildcardCoreAsync(
        Guid webspaceUuid,
        string apex,
        string? email,
        bool force,
        CancellationToken cancellationToken)
    {
        var resolved = (email ?? _config.System.Proxy.AcmeEmail)?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            _logger?.Debug(LoggerTypes.Proxy, "nginx ACME DNS-01 skipped no acme_email");
            return;
        }

        if (_panel is null)
        {
            _logger?.Warning(LoggerTypes.Proxy, "nginx ACME DNS-01 skipped no panel client");
            return;
        }

        if (string.IsNullOrWhiteSpace(apex) || !force && IsCertFresh(apex))
            return;

        EnsureChallengeLayout();
        IoDirectory.CreateDirectory(Path.Combine(AcmeAccountDir, "accounts"));

        if (force)
        {
            try
            {
                if (File.Exists(CertPath(apex))) File.Delete(CertPath(apex));
                if (File.Exists(KeyPath(apex))) File.Delete(KeyPath(apex));
            }
            catch
            {
                // ignore
            }
        }

        lock (_gate)
        {
        }

        try
        {
            var acme = await CreateAcmeContextAsync(resolved, cancellationToken);
            await IssueWildcardAsync(acme, webspaceUuid, apex, cancellationToken);
            _logger?.Info(LoggerTypes.Proxy, $"nginx ACME DNS-01 issued wildcard for {apex}");
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Proxy, $"nginx ACME DNS-01 {apex}: {ex.Message}");
        }
    }

    /// <summary>Read NotAfter for an issued nginx cert, if present.</summary>
    public static DateTimeOffset? GetCertNotAfter(string domain) =>
        GetCertNotAfterFromFile(CertPath(domain));

    public static DateTimeOffset? GetCertNotAfterFromFile(string certPath)
    {
        if (!File.Exists(certPath))
            return null;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(certPath);
            return new DateTimeOffset(cert.NotAfter.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }

    private async Task IssueAsync(string email, List<string> domains, CancellationToken ct)
    {
        var acme = await CreateAcmeContextAsync(email, ct);

        foreach (var domain in domains)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await IssueOneAsync(acme, domain, ct);
                _logger?.Info(LoggerTypes.Proxy, $"nginx ACME issued cert for {domain}");
            }
            catch (Exception ex)
            {
                _logger?.Warning(LoggerTypes.Proxy, $"nginx ACME {domain}: {ex.Message}");
            }
        }
    }

    private async Task<AcmeContext> CreateAcmeContextAsync(string email, CancellationToken ct)
    {
        var staging = _config.System.Proxy.AcmeStaging;
        var server = staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
        var accountDir = Path.Combine(AcmeAccountDir, "accounts");
        IoDirectory.CreateDirectory(accountDir);
        var accountKeyPath = Path.Combine(accountDir, AccountFileName(email, staging));

        IKey accountKey;
        if (File.Exists(accountKeyPath))
            accountKey = KeyFactory.FromPem(await File.ReadAllTextAsync(accountKeyPath, ct));
        else
        {
            accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            await File.WriteAllTextAsync(accountKeyPath, accountKey.ToPem(), ct);
        }

        var acme = new AcmeContext(server, accountKey);
        try
        {
            await acme.Account();
        }
        catch
        {
            await acme.NewAccount(email, true);
        }

        return acme;
    }

    private Task IssueOneAsync(AcmeContext acme, string domain, CancellationToken ct) =>
        _events.WithHooksAsync(
            new AcmeIssueBeforeEvent { Domain = domain },
            err => new AcmeIssueAfterEvent { Domain = domain, Error = err },
            token => IssueOneCoreAsync(acme, domain, token),
            ct);

    private async Task IssueOneCoreAsync(AcmeContext acme, string domain, CancellationToken ct)
    {
        var order = await acme.NewOrder([domain]);
        var authz = (await order.Authorizations()).First();
        var httpChallenge = await authz.Http();
        var token = httpChallenge.Token;
        var keyAuth = httpChallenge.KeyAuthz;
        var challengeFile = Path.Combine(ChallengeRoot, token);
        await File.WriteAllTextAsync(challengeFile, keyAuth, ct);
        try
        {
            await httpChallenge.Validate();
            // Poll until valid or failed
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(2000, ct);
                var resource = await authz.Resource();
                if (resource.Status == AuthStatus.Valid)
                    break;
                if (resource.Status == AuthStatus.Invalid)
                    throw new InvalidOperationException($"ACME authorization invalid for {domain}");
            }

            var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var cert = await order.Generate(new CsrInfo
            {
                CommonName = domain,
            }, privateKey);

            var certPem = cert.ToPem();
            var keyPem = privateKey.ToPem();
            await File.WriteAllTextAsync(CertPath(domain), certPem, ct);
            await File.WriteAllTextAsync(KeyPath(domain), keyPem, ct);
        }
        finally
        {
            try { File.Delete(challengeFile); } catch { /* ignore */ }
        }
    }

    private async Task IssueWildcardAsync(AcmeContext acme, Guid webspaceUuid, string apex, CancellationToken ct)
    {
        if (_panel is null)
            throw new InvalidOperationException("Panel client required for DNS-01");

        var names = new[] { apex, "*." + apex };
        var order = await acme.NewOrder(names);
        var authzs = (await order.Authorizations()).ToList();
        var placed = new List<(string name, string content)>();

        try
        {
            foreach (var authz in authzs)
            {
                ct.ThrowIfCancellationRequested();
                var resource = await authz.Resource();
                var identifier = resource.Identifier?.Value?.Trim().ToLowerInvariant() ?? apex;
                var host = identifier.StartsWith("*.", StringComparison.Ordinal)
                    ? identifier[2..]
                    : identifier;
                var recordName = "_acme-challenge." + host;

                var dnsChallenge = await authz.Dns();
                var txt = acme.AccountKey.DnsTxt(dnsChallenge.Token);
                await _panel.AcmeDnsAsync(webspaceUuid, "set", recordName, txt, ct);
                placed.Add((recordName, txt));

                // Allow DNS propagation before asking LE to check.
                await Task.Delay(5000, ct);
                await dnsChallenge.Validate();

                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(3000, ct);
                    var status = await authz.Resource();
                    if (status.Status == AuthStatus.Valid)
                        break;
                    if (status.Status == AuthStatus.Invalid)
                        throw new InvalidOperationException($"ACME DNS-01 authorization invalid for {identifier}");
                }
            }

            var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var cert = await order.Generate(new CsrInfo
            {
                CommonName = apex,
            }, privateKey);

            await File.WriteAllTextAsync(CertPath(apex), cert.ToPem(), ct);
            await File.WriteAllTextAsync(KeyPath(apex), privateKey.ToPem(), ct);
        }
        finally
        {
            foreach (var (name, content) in placed)
            {
                try
                {
                    await _panel.AcmeDnsAsync(webspaceUuid, "clear", name, content, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(LoggerTypes.Proxy, $"ACME DNS-01 clear {name}: {ex.Message}");
                }
            }
        }
    }
}
