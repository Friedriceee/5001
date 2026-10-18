using JiKeDa.Identity.Api.Common;
using JiKeDa.Identity.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Identity.Api.Features.Accounts;

public enum AccountOutcome
{
    Success,
    NotFound,
    InvalidRole,
}

public sealed record AccountResult(
    AccountOutcome Outcome,
    AccountResponse? Account,
    string? Message);

/// <summary>
/// Account management for administrators: list (paged, role filter),
/// enable/disable, controlled role change. Migrated from the monolith Admin feature.
/// </summary>
public sealed class AccountService
{
    private readonly IdentityDbContext _db;

    public AccountService(IdentityDbContext db) => _db = db;

    public async Task<PagedResult<AccountResponse>> ListAsync(
        string? role,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 100 ? 20 : pageSize;

        var query = _db.Accounts.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(role) && Enum.TryParse<AccountRole>(role, true, out var parsedRole))
            query = query.Where(a => a.Role == parsedRole);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AccountResponse(
                a.Id, a.Username, a.Role.ToString(), a.ProfileId, a.IsActive, a.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResult<AccountResponse>(items, page, pageSize, total);
    }

    public async Task<AccountResult> SetStatusAsync(
        int accountId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
            return new AccountResult(AccountOutcome.NotFound, null, "Account not found");

        account.IsActive = isActive;
        await _db.SaveChangesAsync(cancellationToken);
        return new AccountResult(AccountOutcome.Success, ToResponse(account), null);
    }

    public async Task<AccountResult> SetRoleAsync(
        int accountId,
        string role,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AccountRole>(role, true, out var parsedRole))
            return new AccountResult(AccountOutcome.InvalidRole, null, "Invalid role");

        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
            return new AccountResult(AccountOutcome.NotFound, null, "Account not found");

        account.Role = parsedRole;
        await _db.SaveChangesAsync(cancellationToken);
        return new AccountResult(AccountOutcome.Success, ToResponse(account), null);
    }

    private static AccountResponse ToResponse(Account account) => new(
        account.Id, account.Username, account.Role.ToString(),
        account.ProfileId, account.IsActive, account.CreatedAtUtc);
}
