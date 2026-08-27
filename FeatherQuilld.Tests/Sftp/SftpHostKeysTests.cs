using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Config.Sftp;
using FeatherQuilld.Utils.Sftp;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Tests.Sftp;

public sealed class SftpHostKeysTests
{
    [Fact]
    public void EnsureHostKey_Ed25519_ProducesOpenSshKeyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-sftp-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig
            {
                System = { RootDirectory = root },
                Sftp = new SftpConfig
                {
                    Enabled = true,
                    KeyAlgorithm = "ssh-ed25519",
                },
            };

            var material = SftpHostKeys.EnsureHostKey(config);

            Assert.Equal(SftpHostKeys.AlgoEd25519, material.Algorithm);
            Assert.True(File.Exists(material.PrivateKeyPath));
            Assert.True(File.Exists(material.PublicKeyPath!));
            Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", File.ReadAllText(material.PrivateKeyPath));
            Assert.StartsWith("ssh-ed25519 ", File.ReadAllText(material.PublicKeyPath!));
            Assert.False(string.IsNullOrWhiteSpace(material.FingerprintSha256));
            Assert.StartsWith("SHA256:", material.FingerprintSha256!);

            // Idempotent — second call reuses the same files.
            var again = SftpHostKeys.EnsureHostKey(config);
            Assert.Equal(material.PrivateKeyPath, again.PrivateKeyPath);
            Assert.Equal(material.FingerprintSha256, again.FingerprintSha256);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void EnsureHostKey_Rsa_ProducesFxSshXml()
    {
        var root = Path.Combine(Path.GetTempPath(), "fq-sftp-rsa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig
            {
                System = { RootDirectory = root },
                Sftp = new SftpConfig { KeyAlgorithm = "ssh-rsa" },
            };

            var material = SftpHostKeys.EnsureHostKey(config);
            Assert.Equal(SftpHostKeys.AlgoRsa, material.Algorithm);
            Assert.True(File.Exists(material.PrivateKeyPath));
            var body = File.ReadAllText(material.PrivateKeyPath).Trim();
            Assert.False(string.IsNullOrWhiteSpace(body));
            // FxSsh 1.2 emits a base64 blob (not <RSAKeyValue> XML).
            Assert.True(body.Length > 32);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
