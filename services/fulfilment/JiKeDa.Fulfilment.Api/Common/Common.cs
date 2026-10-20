using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JiKeDa.Fulfilment.Api.Common;

public class ApiResult<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Message { get; set; }
    public string? Code { get; set; }
    public static ApiResult<T> Ok(T data) => new() { Success = true, Data = data };
    public static ApiResult<T> Fail(string message, string? code = null) => new() { Message = message, Code = code };
}

public static class ApiResult
{
    public static ApiResult<object> Fail(string message, string? code = null) => ApiResult<object>.Fail(message, code);
}

public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        logger.LogError(context.Exception, "Unhandled exception at {Path}", context.HttpContext.Request.Path);
        context.Result = new ObjectResult(ApiResult.Fail("Internal server error")) { StatusCode = 500 };
        context.ExceptionHandled = true;
    }
}

public static class Roles
{
    public const string Customer = "Customer";
    public const string Merchant = "Merchant";
    public const string Rider = "Rider";
    public const string Service = "Service";
}

public static class ClaimsPrincipalExtensions
{
    public const string ProfileIdClaim = "profile_id";
    public static int GetProfileId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ProfileIdClaim), out var id)
            ? id : throw new InvalidOperationException("Token does not contain a valid profile_id claim.");
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "JiKeDa";
    public string Audience { get; set; } = "JiKeDa.Client";
    public string Key { get; set; } = string.Empty;
}
