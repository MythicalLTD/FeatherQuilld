using System.Security.Cryptography.X509Certificates;
using FeatherQuilld.Utils.Config.Api;
using IoPath = System.IO.Path;

namespace FeatherQuilld.Utils.Startup;

/// <summary>Loads the daemon API TLS certificate from PEM or PKCS#12 (PFX) paths.</summary>
public static class ApiSslCertificate
{
    public static X509Certificate2 Load(ApiSslConfig ssl, string? configBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(ssl);

        var certPath = ResolvePath(ssl.Cert, configBaseDirectory);
        if (string.IsNullOrWhiteSpace(certPath))
            throw new InvalidOperationException("api.ssl.cert is required when api.ssl.enabled is true.");

        if (!File.Exists(certPath))
            throw new InvalidOperationException($"API SSL certificate not found: {certPath}");

        var isPkcs12 = IsPkcs12Path(certPath) || string.IsNullOrWhiteSpace(ssl.Key);
        if (isPkcs12)
        {
            if (!IsPkcs12Path(certPath) && string.IsNullOrWhiteSpace(ssl.Key))
            {
                throw new InvalidOperationException(
                    $"api.ssl.key is required for PEM certificates (cert={certPath}). " +
                    "For PKCS#12 use a .pfx/.p12 cert path.");
            }

            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(
                    certPath,
                    ssl.Password ?? "",
                    X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load PKCS#12 certificate from {certPath}: {ex.Message}", ex);
            }
        }

        var keyPath = ResolvePath(ssl.Key, configBaseDirectory);
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            throw new InvalidOperationException($"API SSL private key not found: {keyPath}");

        try
        {
            return X509Certificate2.CreateFromPemFile(certPath, keyPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load PEM certificate from {certPath} / {keyPath}: {ex.Message}", ex);
        }
    }

    internal static bool IsPkcs12Path(string path)
    {
        var ext = IoPath.GetExtension(path);
        return ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".p12", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolvePath(string? path, string? configBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var trimmed = path.Trim();
        if (IoPath.IsPathRooted(trimmed))
            return IoPath.GetFullPath(trimmed);

        var baseDir = string.IsNullOrWhiteSpace(configBaseDirectory)
            ? Directory.GetCurrentDirectory()
            : configBaseDirectory;
        return IoPath.GetFullPath(IoPath.Combine(baseDir, trimmed));
    }
}
