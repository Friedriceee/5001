using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JiKeDa.Catalog.Api.Common;

/// <summary>Converts unhandled exceptions into a 500 ApiResult instead of leaking stack traces.</summary>
public class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        _logger.LogError(context.Exception, "Unhandled exception: {Path}", context.HttpContext.Request.Path);
        context.Result = new ObjectResult(ApiResult.Fail("Internal server error"))
        {
            StatusCode = StatusCodes.Status500InternalServerError,
        };
        context.ExceptionHandled = true;
    }
}
