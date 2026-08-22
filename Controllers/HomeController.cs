using FeatherQuilld.Utils.Config;
using FeatherQuilld.Utils.Startup;
using FeatherQuilld.Utils.Web;
using Microsoft.AspNetCore.Mvc;

namespace FeatherQuilld.Controllers;

/// <summary>
/// Root landing page for the daemon HTTP surface.
/// </summary>
public sealed class HomeController : ControllerBase
{
    /// <summary>Serves the main page at <c>/</c>.</summary>
    [HttpGet("/")]
    [Produces("text/html")]
    public ContentResult Index([FromServices] Config config) =>
        Content(HomePage.Render(config.AppName, StartupBanner.Version, config.Api.Docs.Enabled), "text/html");
}
