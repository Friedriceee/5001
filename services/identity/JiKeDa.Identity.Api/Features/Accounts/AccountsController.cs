using JiKeDa.Identity.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiKeDa.Identity.Api.Features.Accounts;

[ApiController]
[Route("api/accounts")]
[Authorize(Roles = nameof(AccountRole.Administrator))]
public sealed class AccountsController : ControllerBase
{
    private readonly AccountService _accounts;

    public AccountsController(AccountService accounts) => _accounts = accounts;

    [HttpGet]
    public async Task<ActionResult<ApiResult<PagedResult<AccountResponse>>>> List(
        [FromQuery] string? role,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _accounts.ListAsync(role, page, pageSize, cancellationToken);
        return Ok(ApiResult<PagedResult<AccountResponse>>.Ok(result));
    }

    [HttpPut("{accountId:int}/status")]
    public async Task<ActionResult<ApiResult<AccountResponse>>> SetStatus(
        int accountId,
        [FromBody] UpdateAccountStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _accounts.SetStatusAsync(accountId, request.IsActive, cancellationToken);
        return Map(result);
    }

    [HttpPut("{accountId:int}/role")]
    public async Task<ActionResult<ApiResult<AccountResponse>>> SetRole(
        int accountId,
        [FromBody] UpdateAccountRoleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _accounts.SetRoleAsync(accountId, request.Role, cancellationToken);
        return Map(result);
    }

    private ActionResult<ApiResult<AccountResponse>> Map(AccountResult result) =>
        result.Outcome switch
        {
            AccountOutcome.Success => Ok(ApiResult<AccountResponse>.Ok(result.Account!)),
            AccountOutcome.NotFound => NotFound(ApiResult<AccountResponse>.Fail(result.Message!)),
            _ => BadRequest(ApiResult<AccountResponse>.Fail(result.Message!)),
        };
}
