using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FxSsh;
using AppConfig = FeatherQuilld.Utils.Config.Config;
using AppLogger = FeatherQuilld.Utils.Logger.Logger;
using FeatherQuilld.Utils.Logger;

namespace FeatherQuilld.Utils.Sftp;

/// <summary>
/// Generates and resolves SFTP host keys according to <c>sftp.key_algorithm</c>.
/// </summary>
public static class SftpHostKeys
{
    public const string AlgoEd25519 = "ssh-ed25519";
    public const string AlgoRsa = "ssh-rsa";

    public sealed record HostKeyMaterial(
        string Algorithm,
        string PrivateKeyPath,
        string? PublicKeyPath,
        string? FingerprintSha256);

    public static string NormalizeAlgorithm(string? configured)
    {
        var algo = (configured ?? AlgoEd25519).Trim().ToLowerInvariant();
        return algo switch
        {
            "ed25519" or "ssh-ed25519" => AlgoEd25519,
            "rsa" or "ssh-rsa" => AlgoRsa,
            "dss" or "ssh-dss" => "ssh-dss",
            _ => algo,
        };
    }

    /// <summary>
    /// Ensures a host key exists for the configured algorithm under <c>{Root}/.sftp/</c>.
    /// </summary>
    public static HostKeyMaterial EnsureHostKey(AppConfig config, AppLogger? logger = null)
    {
        var dir = Path.Combine(config.System.RootDirectory, ".sftp");
        Directory.CreateDirectory(dir);

        var algo = NormalizeAlgorithm(config.Sftp.KeyAlgorithm);
        return algo switch
        {
            AlgoEd25519 => EnsureEd25519(dir, logger),
            AlgoRsa => EnsureRsa(dir, logger),
            "ssh-dss" => EnsureRsa(dir, logger, dssFallback: true),
            _ => throw new InvalidOperationException(
                $"Unsupported sftp.key_algorithm '{config.Sftp.KeyAlgorithm}'. Use ssh-ed25519 or ssh-rsa."),
        };
    }

    private static HostKeyMaterial EnsureEd25519(string dir, AppLogger? logger)
    {
        var privatePath = Path.Combine(dir, "id_ed25519");
        var publicPath = privatePath + ".pub";
        var fingerprintPath = privatePath + ".fingerprint";

        if (!File.Exists(privatePath) || !File.Exists(publicPath))
        {
            if (File.Exists(privatePath))
                File.Delete(privatePath);
            if (File.Exists(publicPath))
                File.Delete(publicPath);

            RunSshKeygen(privatePath);
            logger?.Info(LoggerTypes.Application, $"Generated SFTP ed25519 host key → {privatePath}");
        }

        var fingerprint = File.Exists(fingerprintPath)
            ? File.ReadAllText(fingerprintPath).Trim()
            : ComputeAndPersistFingerprint(publicPath, fingerprintPath, logger);

        return new HostKeyMaterial(AlgoEd25519, privatePath, publicPath, fingerprint);
    }

    private static HostKeyMaterial EnsureRsa(string dir, AppLogger? logger, bool dssFallback = false)
    {
        // FxSsh host keys use XML RSA material (dss not generated — fall back to RSA).
        var path = Path.Combine(dir, "id_rsa.xml");
        if (!File.Exists(path))
        {
            var xml = KeyUtils.GeneratePrivateKey("ssh-rsa");
            File.WriteAllText(path, xml);
            logger?.Info(LoggerTypes.Application,
                dssFallback
                    ? $"ssh-dss requested but unsupported; generated RSA host key → {path}"
                    : $"Generated SFTP RSA host key → {path}");
        }

        return new HostKeyMaterial(AlgoRsa, path, PublicKeyPath: null, FingerprintSha256: null);
    }

    private static void RunSshKeygen(string privatePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            ArgumentList = { "-t", "ed25519", "-f", privatePath, "-N", "", "-q", "-C", "featherquilld-sftp" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh-keygen for SFTP host key.");
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException("ssh-keygen timed out generating ed25519 host key.");
        }

        if (proc.ExitCode != 0 || !File.Exists(privatePath))
            throw new InvalidOperationException(
                $"ssh-keygen failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    private static string ComputeAndPersistFingerprint(string publicPath, string fingerprintPath, AppLogger? logger)
    {
        var line = File.ReadAllText(publicPath).Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException("Invalid OpenSSH public key format.");

        var blob = Convert.FromBase64String(parts[1]);
        var hash = SHA256.HashData(blob);
        var fingerprint = "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        File.WriteAllText(fingerprintPath, fingerprint + Environment.NewLine, Encoding.UTF8);
        logger?.Info(LoggerTypes.Application, $"SFTP host key fingerprint {fingerprint}");
        return fingerprint;
    }
}
