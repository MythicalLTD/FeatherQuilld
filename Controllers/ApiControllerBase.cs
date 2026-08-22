using Microsoft.AspNetCore.Mvc;

namespace FeatherQuilld.Controllers;

/// <summary>
/// Base API controller. All endpoints live under <c>/api</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase;
