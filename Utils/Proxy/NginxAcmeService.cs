using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using AuthStatus = Certes.Acme.Resource.AuthorizationStatus;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;
using IoDirectory = System.IO.Directory;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Issues/renews Let's Encrypt certificates for nginx via Certes HTTP-01.
/// Challenge files: <c>{RootDirectory}/acme/www/.well-known/acme-challenge/</c>
/// Certs: <c>/etc/featherquilld/certs/{domain}.crt|.key</c>
/// </summary>
public sealed class NginxAcmeService
{
    public const string DefaultCertDirectory = "/etc/featherquilld/certs";

    private readonly AppConfig _config;
    private readonly AppLogger? _logger;
    private readonly object _gate = new();

    public NginxAcmeService(AppConfig config, AppLogger? logger = null)
    {
        _config = config;
        _logger = logger;
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
    public async Task EnsureCertsAsync(
        IEnumerable<string> domains,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        var email = _config.System.Proxy.AcmeEmail?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger?.Debug(LoggerTypes.Proxy, "nginx ACME skipped — no acme_email");
            return;
        }

        EnsureChallengeLayout();
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
            await IssueAsync(email, needed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LoggerTypes.Proxy, $"nginx ACME failed: {ex.Message}");
        }
    }

    /// <summary>Read NotAfter for an issued nginx cert, if present.</summary>
    public static DateTimeOffset? GetCertNotAfter(string domain)
    {
        var crt = CertPath(domain);
        if (!File.Exists(crt))
            return null;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(crt);
            return new DateTimeOffset(cert.NotAfter.ToUniversalTime());
        }
        catch
        {
            return null;
        }
    }

    private async Task IssueAsync(string email, List<string> domains, CancellationToken ct)
    {
        var staging = _config.System.Proxy.AcmeStaging;
        var server = staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
        var accountKeyPath = Path.Combine(AcmeAccountDir, staging ? "account-staging.pem" : "account.pem");

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

    private async Task IssueOneAsync(AcmeContext acme, string domain, CancellationToken ct)
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
}
