using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FeatherQuilld.Utils.Config.Api;
using FeatherQuilld.Utils.Startup;

namespace FeatherQuilld.Tests.Startup;

public class ApiSslCertificateTests
{
    [Fact]
    public void Load_PemPair_Succeeds()
    {
        using var dir = new TempDir();
        var (certPath, keyPath) = WritePemPair(dir.Path, "CN=pem-test");

        using var loaded = ApiSslCertificate.Load(
            new ApiSslConfig { Enabled = true, Cert = certPath, Key = keyPath },
            configBaseDirectory: null);

        Assert.Equal("CN=pem-test", loaded.Subject);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Load_Pfx_Succeeds()
    {
        using var dir = new TempDir();
        var pfxPath = Path.Combine(dir.Path, "server.pfx");
        using (var cert = CreateSelfSigned("CN=pfx-test"))
        {
            File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, "secret"));
        }

        using var loaded = ApiSslCertificate.Load(
            new ApiSslConfig
            {
                Enabled = true,
                Cert = pfxPath,
                Key = "",
                Password = "secret",
            },
            configBaseDirectory: null);

        Assert.Equal("CN=pfx-test", loaded.Subject);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Load_RelativePem_ResolvesAgainstBaseDir()
    {
        using var dir = new TempDir();
        WritePemPair(dir.Path, "CN=rel-test", certName: "cert.pem", keyName: "key.pem");

        using var loaded = ApiSslCertificate.Load(
            new ApiSslConfig { Enabled = true, Cert = "cert.pem", Key = "key.pem" },
            configBaseDirectory: dir.Path);

        Assert.Equal("CN=rel-test", loaded.Subject);
    }

    [Fact]
    public void Load_MissingCert_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ApiSslCertificate.Load(
                new ApiSslConfig
                {
                    Enabled = true,
                    Cert = "/tmp/does-not-exist-" + Guid.NewGuid().ToString("N") + ".pem",
                    Key = "/tmp/also-missing.key",
                },
                null));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsPkcs12Path_DetectsExtensions()
    {
        Assert.True(ApiSslCertificate.IsPkcs12Path("a.pfx"));
        Assert.True(ApiSslCertificate.IsPkcs12Path("b.P12"));
        Assert.False(ApiSslCertificate.IsPkcs12Path("c.pem"));
    }

    private static (string CertPath, string KeyPath) WritePemPair(
        string dir,
        string subject,
        string certName = "fullchain.pem",
        string keyName = "privkey.pem")
    {
        using var cert = CreateSelfSigned(subject);
        var certPath = Path.Combine(dir, certName);
        var keyPath = Path.Combine(dir, keyName);
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        using var key = cert.GetECDsaPrivateKey()
                       ?? throw new InvalidOperationException("expected ECDSA key");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fq-ssl-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
