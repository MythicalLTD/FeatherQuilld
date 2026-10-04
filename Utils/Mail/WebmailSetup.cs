using System.Security.Cryptography;
using System.Text;
using AppConfig = FeatherQuilld.Utils.Config.Config;

namespace FeatherQuilld.Utils.Mail;

/// <summary>Compose, SSO secret, and Roundcube token.php for node webmail.</summary>
public static class WebmailSetup
{
    public static string NormalizeHostname(string hostname) =>
        hostname.Trim().TrimEnd('.').ToLowerInvariant();

    public static bool IsValidHostname(string hostname)
    {
        hostname = NormalizeHostname(hostname);
        if (hostname.Length is < 1 or > 253)
            return false;
        if (hostname.Contains('/') || hostname.Contains(':') || hostname.Contains(' '))
            return false;
        return hostname.Contains('.') && !hostname.StartsWith('.') && !hostname.EndsWith('.');
    }

    public static string BuildCompose(AppConfig config)
    {
        var imapPort = config.System.Mail.ImapPort > 0 ? config.System.Mail.ImapPort : 993;
        var smtpPort = config.System.Mail.SmtpPort > 0 ? config.System.Mail.SmtpPort : 587;

        return $$"""
            services:
              webmail:
                image: {{WebmailPaths.Image}}
                container_name: {{WebmailPaths.ContainerName}}
                ports:
                  - "127.0.0.1:{{WebmailPaths.DefaultPort}}:80"
                volumes:
                  - ./data:/var/roundcube/db
                  - ./custom/token.php:/var/www/html/token.php:ro
                  - ./sso.secret:/var/www/html/sso.secret:ro
                environment:
                  - ROUNDCUBEMAIL_DEFAULT_HOST=ssl://host.docker.internal
                  - ROUNDCUBEMAIL_DEFAULT_PORT={{imapPort}}
                  - ROUNDCUBEMAIL_SMTP_SERVER=tls://host.docker.internal
                  - ROUNDCUBEMAIL_SMTP_PORT={{smtpPort}}
                extra_hosts:
                  - "host.docker.internal:host-gateway"
                restart: unless-stopped
            """;
    }

    /// <summary>Ensures SSO secret exists; returns the secret (create or read).</summary>
    public static string EnsureSsoSecret(AppConfig config)
    {
        Directory.CreateDirectory(WebmailPaths.Root(config));
        var path = WebmailPaths.SsoSecretFile(config);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
                return existing;
        }

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, secret + "\n");
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* unsupported FS */ }
        }
        return secret;
    }

    public static bool HasSsoSecret(AppConfig config)
    {
        var path = WebmailPaths.SsoSecretFile(config);
        if (!File.Exists(path))
            return false;
        return File.ReadAllText(path).Trim().Length >= 32;
    }

    public static void EnsureCustomFiles(AppConfig config)
    {
        Directory.CreateDirectory(WebmailPaths.CustomDir(config));
        EnsureSsoSecret(config);
        File.WriteAllText(WebmailPaths.TokenPhpFile(config), TokenPhpSource, Encoding.UTF8);
    }

    public static string PublicUrl(string hostname) =>
        "https://" + NormalizeHostname(hostname);

    /// <summary>
    /// Roundcube SSO entrypoint: verifies a short-lived HMAC token minted by FeatherPanel,
    /// then logs into IMAP. Does not accept plaintext pass= query params.
    /// </summary>
    public const string TokenPhpSource = """
        <?php
        declare(strict_types=1);

        $token = (string) ($_GET['token'] ?? '');
        if ($token === '' || !str_contains($token, '.')) {
            http_response_code(400);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Missing or invalid token\n";
            exit;
        }

        $secretFile = __DIR__ . '/sso.secret';
        if (!is_readable($secretFile)) {
            http_response_code(503);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Webmail SSO is not configured\n";
            exit;
        }

        $secret = trim((string) file_get_contents($secretFile));
        if ($secret === '') {
            http_response_code(503);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Webmail SSO secret missing\n";
            exit;
        }

        [$payloadB64, $sigB64] = explode('.', $token, 2);
        $payloadRaw = feather_webmail_b64url_decode($payloadB64);
        $sigRaw = feather_webmail_b64url_decode($sigB64);
        if ($payloadRaw === null || $sigRaw === null) {
            http_response_code(400);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Malformed token\n";
            exit;
        }

        $expected = hash_hmac('sha256', $payloadB64, $secret, true);
        if (!hash_equals($expected, $sigRaw)) {
            http_response_code(401);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Invalid token signature\n";
            exit;
        }

        $data = json_decode($payloadRaw, true);
        if (!is_array($data)) {
            http_response_code(400);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Invalid token payload\n";
            exit;
        }

        $exp = (int) ($data['exp'] ?? 0);
        if ($exp < time()) {
            http_response_code(401);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Token expired\n";
            exit;
        }

        $user = (string) ($data['u'] ?? '');
        $passEnc = (string) ($data['p'] ?? '');
        $host = (string) ($data['h'] ?? '');
        $port = (int) ($data['o'] ?? 993);
        $enc = strtolower((string) ($data['e'] ?? 'ssl'));

        $pass = feather_webmail_decrypt_pass($passEnc, $secret);
        if ($user === '' || $pass === null || $host === '') {
            http_response_code(400);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Token missing credentials\n";
            exit;
        }

        $imapHost = match ($enc) {
            'ssl' => 'ssl://' . $host,
            'tls', 'starttls' => 'tls://' . $host,
            default => $host,
        };

        define('INSTALL_PATH', realpath(__DIR__) . '/');

        if (!file_exists(__DIR__ . '/program/include/iniset.php')) {
            http_response_code(503);
            header('Content-Type: text/plain; charset=utf-8');
            echo "Roundcube is not installed\n";
            exit;
        }

        require_once __DIR__ . '/program/include/iniset.php';

        $rcmail = rcmail::get_instance();
        $rcmail->config->set('default_host', $imapHost);
        $rcmail->config->set('default_port', $port > 0 ? $port : 993);

        if ($rcmail->login($user, $pass, $imapHost, true)) {
            $rcmail->session->set('auth_type', 'feather_sso');
            header('Location: ./?_task=mail');
            exit;
        }

        http_response_code(401);
        header('Content-Type: text/html; charset=utf-8');
        echo '<!DOCTYPE html><html><body><p>Webmail login failed. Check IMAP host credentials.</p>';
        echo '<p><a href="./">Try Roundcube login</a></p></body></html>';

        function feather_webmail_b64url_decode(string $data): ?string
        {
            $remainder = strlen($data) % 4;
            if ($remainder) {
                $data .= str_repeat('=', 4 - $remainder);
            }
            $raw = base64_decode(strtr($data, '-_', '+/'), true);
            return $raw === false ? null : $raw;
        }

        function feather_webmail_decrypt_pass(string $blob, string $secret): ?string
        {
            $raw = feather_webmail_b64url_decode($blob);
            if ($raw === null || strlen($raw) < 29) {
                return null;
            }
            $iv = substr($raw, 0, 12);
            $tag = substr($raw, 12, 16);
            $cipher = substr($raw, 28);
            $key = hash('sha256', $secret, true);
            $plain = openssl_decrypt($cipher, 'aes-256-gcm', $key, OPENSSL_RAW_DATA, $iv, $tag);
            return $plain === false ? null : $plain;
        }
        """;
}
