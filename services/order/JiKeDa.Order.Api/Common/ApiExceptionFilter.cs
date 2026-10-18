using JiKeDa.Order.Api.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JiKeDa.Order.Api.Common;

/// <summary>
/// Converts unhandled exceptions into an ApiResult instead of leaking stack traces. A Catalog outage
/// becomes 503 so clients know to retry; anything else is a 500.
/// </summary>
public class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is CatalogUnavailableException)
        {
            _logger.LogWarning(context.Exception, "Catalog unavailable: {Path}", context.HttpContext.Request.Path);
            context.Result = new ObjectResult(ApiResult.Fail("The menu service is temporarily unavailable. Please try again.", "ServiceUnavailable"))
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }
        else
        {
            _logger.LogError(context.Exception, "Unhandled exception: {Path}", context.HttpContext.Request.Path);
            context.Result = new ObjectResult(ApiResult.Fail("Internal server error"))
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
        }
        context.ExceptionHandled = true;
    }
}
