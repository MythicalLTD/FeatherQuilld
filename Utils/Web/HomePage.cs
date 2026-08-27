using System.Net;

namespace FeatherQuilld.Utils.Web;

/// <summary>
/// Root landing page HTML for the daemon HTTP surface.
/// </summary>
public static class HomePage
{
    private const string IconBook =
        """<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>""";

    private const string IconPulse =
        """<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>""";

    private const string IconInfo =
        """<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/></svg>""";

    private const string IconBlocks =
        """<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="7" height="7" x="14" y="3" rx="1"/><path d="M10 21V8a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v12a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-5a1 1 0 0 0-1-1H3"/></svg>""";

    private const string IconArrow =
        """<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14"/><path d="m12 5 7 7-7 7"/></svg>""";

    public static string Render(string appName, string version, bool docsEnabled)
    {
        var docsLink = docsEnabled
            ? Link("/scalar", IconBook, "API Docs", "Interactive reference")
            : "";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{WebUtility.HtmlEncode(appName)}}</title>
              <style>
                :root {
                  --accent: #2dd4bf;
                  --text: #fafafa;
                  --muted: #71717a;
                  --border: #27272a;
                  --surface: #18181b;
                  --bg: #09090b;
                }

                * { box-sizing: border-box; margin: 0; padding: 0; }

                body {
                  min-height: 100vh;
                  font-family: ui-sans-serif, system-ui, -apple-system, sans-serif;
                  background: var(--bg);
                  color: var(--text);
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  padding: 2rem 1.25rem;
                  -webkit-font-smoothing: antialiased;
                }

                main {
                  width: min(420px, 100%);
                }

                header {
                  margin-bottom: 2rem;
                }

                .brand {
                  display: flex;
                  align-items: baseline;
                  justify-content: space-between;
                  gap: 1rem;
                  margin-bottom: 0.375rem;
                }

                h1 {
                  font-size: 1.375rem;
                  font-weight: 600;
                  letter-spacing: -0.02em;
                }

                .version {
                  font-size: 0.75rem;
                  font-variant-numeric: tabular-nums;
                  color: var(--muted);
                }

                .tagline {
                  font-size: 0.8125rem;
                  color: var(--muted);
                }

                .status {
                  display: flex;
                  align-items: center;
                  gap: 0.5rem;
                  margin-top: 1rem;
                  font-size: 0.8125rem;
                  color: var(--muted);
                }

                .status-dot {
                  width: 6px;
                  height: 6px;
                  border-radius: 50%;
                  background: var(--accent);
                }

                nav {
                  border: 1px solid var(--border);
                  border-radius: 0.5rem;
                  overflow: hidden;
                  background: var(--surface);
                }

                .link {
                  display: flex;
                  align-items: center;
                  gap: 0.875rem;
                  padding: 0.875rem 1rem;
                  text-decoration: none;
                  color: inherit;
                  border-bottom: 1px solid var(--border);
                  transition: background 0.12s;
                }

                .link:last-child { border-bottom: none; }

                .link:hover { background: rgba(255, 255, 255, 0.03); }

                .link-icon {
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  width: 2rem;
                  height: 2rem;
                  flex-shrink: 0;
                  border-radius: 0.375rem;
                  background: rgba(45, 212, 191, 0.08);
                  color: var(--accent);
                }

                .link-body {
                  flex: 1;
                  min-width: 0;
                }

                .link-title {
                  display: block;
                  font-size: 0.875rem;
                  font-weight: 500;
                }

                .link-desc {
                  display: block;
                  font-size: 0.75rem;
                  color: var(--muted);
                  margin-top: 0.125rem;
                }

                .link-arrow {
                  flex-shrink: 0;
                  color: var(--muted);
                  opacity: 0;
                  transition: opacity 0.12s, transform 0.12s;
                }

                .link:hover .link-arrow {
                  opacity: 1;
                  transform: translateX(2px);
                }

                footer {
                  margin-top: 1.5rem;
                  font-size: 0.75rem;
                  color: var(--muted);
                  text-align: center;
                }
              </style>
            </head>
            <body>
              <main>
                <header>
                  <div class="brand">
                    <h1>{{WebUtility.HtmlEncode(appName)}}</h1>
                    <span class="version">v{{WebUtility.HtmlEncode(version)}}</span>
                  </div>
                  <p class="tagline">Light as a feather, sharp as a quill.</p>
                  <div class="status">
                    <span class="status-dot"></span>
                    Running
                  </div>
                </header>

                <nav>
                  {{docsLink}}
                  {{Link("/api/system/health", IconPulse, "Health", "Liveness + panel status")}}
                  {{Link("/api/system", IconInfo, "System", "OS / CPU / version")}}
                  {{Link("/api/system/utilization", IconInfo, "Utilization", "CPU / memory / disk")}}
                  {{Link("/api/system/diagnostics", IconBlocks, "Diagnostics", "Self-test checks")}}
                  {{Link("/api/system/plugins", IconBlocks, "Plugins", "Loaded extensions")}}
                </nav>

                <footer>© {{DateTime.UtcNow.Year}} {{WebUtility.HtmlEncode(appName)}}</footer>
              </main>
            </body>
            </html>
            """;
    }

    private static string Link(string href, string icon, string title, string desc) =>
        $"""
          <a class="link" href="{WebUtility.HtmlEncode(href)}">
            <span class="link-icon">{icon}</span>
            <span class="link-body">
              <span class="link-title">{WebUtility.HtmlEncode(title)}</span>
              <span class="link-desc">{WebUtility.HtmlEncode(desc)}</span>
            </span>
            <span class="link-arrow">{IconArrow}</span>
          </a>
          """;
}
