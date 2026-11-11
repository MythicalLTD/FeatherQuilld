using System.Text;
using FeatherQuilld.Utils.WebSpaces;

namespace FeatherQuilld.Utils.Proxy;

/// <summary>
/// Renders and stores the customer's block page.
///
/// The page is served by Caddy through <c>file_server</c> + <c>templates</c> from a file on disk, NOT inlined in the Caddyfile:
/// Caddy expands <c>{env.X}</c> placeholders in <c>respond</c> bodies (also when escaped with a backslash), which would let a
/// customer read the node's environment. Inside the template file every customer brace is turned into a literal, so the only
/// live template actions are the three we insert ourselves (status, request id, client address).
/// </summary>
public static class WafBlockPageFiles
{
    public const string FileName = "block.html";

    private const string Status = "\u0001S\u0001";
    private const string Id = "\u0001I\u0001";
    private const string Ip = "\u0001P\u0001";

    public const string DefaultHtml = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{status}}</title>
        <style>
          body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1115;color:#e5e7eb;font-family:system-ui,sans-serif}
          main{text-align:center;padding:2rem}
          h1{font-size:4rem;margin:0}
          p{color:#9ca3af}
          small{color:#6b7280}
        </style>
        </head>
        <body>
        <main>
          <h1>{{status}}</h1>
          <p>{{message}}</p>
          <small>Request ID {{id}}</small>
        </main>
        </body>
        </html>
        """;

    /// <summary>Directory holding the block page of one WebSpace.</summary>
    public static string Directory(string rootDirectory, Guid uuid) =>
        Path.Combine(rootDirectory, "proxy", "waf", uuid.ToString("D"));

    /// <summary>Caddy <c>templates</c> source for the page. Pure function, safe for any customer input.</summary>
    public static string Render(string? customHtml, string? message)
    {
        var html = string.IsNullOrWhiteSpace(customHtml) ? DefaultHtml : customHtml;
        html = StripControl(html, keepWhitespace: true);

        html = html.Replace("{{status}}", Status, StringComparison.Ordinal)
                   .Replace("{{id}}", Id, StringComparison.Ordinal)
                   .Replace("{{ip}}", Ip, StringComparison.Ordinal)
                   .Replace("{{message}}", HtmlEscape(StripControl(message ?? "", keepWhitespace: false)), StringComparison.Ordinal);

        // Every brace becomes a literal, so "{{ env ... }}" or "{{include ...}}" in customer HTML is printed, never run.
        html = html.Replace("{", "{{\"{\"}}", StringComparison.Ordinal);

        return html.Replace(Status, "{{placeholder \"http.error.status_code\"}}", StringComparison.Ordinal)
                   .Replace(Id, "{{placeholder \"http.request.uuid\"}}", StringComparison.Ordinal)
                   .Replace(Ip, "{{placeholder \"http.request.remote.host\"}}", StringComparison.Ordinal);
    }

    /// <summary>Write the block page for a space, or remove it when the space no longer needs one.</summary>
    public static void Sync(string rootDirectory, WebSpace space)
    {
        var dir = Directory(rootDirectory, space.Uuid);
        var settings = space.WafSettings;
        try
        {
            if (!space.WafEnabled || settings is null || settings.Mode == "off")
            {
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, recursive: true);
                return;
            }

            System.IO.Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, FileName);
            var body = Render(settings.BlockPage.Html, settings.BlockPage.Message);
            if (!File.Exists(target) || File.ReadAllText(target) != body)
                File.WriteAllText(target, body, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: Caddy then answers with its default error page instead of the custom one.
        }
    }

    private static string StripControl(string s, bool keepWhitespace)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < ' ' && !(keepWhitespace && ch is '\n' or '\r' or '\t'))
                continue;
            if (ch == 0x7f)
                continue;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
}

