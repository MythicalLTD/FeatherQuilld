using FeatherQuilld.Plugins.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FeatherQuilld.Controllers;

/// <summary>Maps plugin Before-hook cancellation to HTTP 403.</summary>
public sealed class PluginHookCancelledExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not PluginHookCancelledException ex)
            return;

        context.Result = new ObjectResult(new
        {
            error = "forbidden",
            message = ex.Message,
            event_name = ex.EventName,
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
        context.ExceptionHandled = true;
    }
}
