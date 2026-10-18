using JiKeDa.Identity.Api.Features.Accounts;
using NUnit.Framework;

namespace JiKeDa.Identity.Tests;

/// <summary>Unit tests for AccountService: list/filter/paginate, enable-disable, role change.</summary>
public class AccountServiceTests : ServiceTestBase
{
    [Test]
    public async Task ListAccounts_filters_by_role()
    {
        await using var db = CreateContext();
        var customers = await new AccountService(db).ListAsync("Customer", 1, 20, CancellationToken.None);

        Assert.That(customers.Items.All(a => a.Role == "Customer"), Is.True);
        Assert.That(customers.Items, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task ListAccounts_paginates()
    {
        await using var db = CreateContext();
        var page = await new AccountService(db).ListAsync(null, 1, 2, CancellationToken.None);

        Assert.That(page.PageSize, Is.EqualTo(2));
        Assert.That(page.Items, Has.Count.LessThanOrEqualTo(2));
        Assert.That(page.TotalCount, Is.GreaterThanOrEqualTo(4)); // 4 seed accounts
    }

    [Test]
    public async Task SetStatus_disables_account()
    {
        await using var db = CreateContext();
        var result = await new AccountService(db).SetStatusAsync(1003, false, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountOutcome.Success));
        Assert.That(result.Account!.IsActive, Is.False);

        await using var verify = CreateContext();
        Assert.That(verify.Accounts.Single(a => a.Id == 1003).IsActive, Is.False);
    }

    [Test]
    public async Task SetStatus_for_missing_account_returns_NotFound()
    {
        await using var db = CreateContext();
        var result = await new AccountService(db).SetStatusAsync(999999, false, CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(AccountOutcome.NotFound));
    }

    [Test]
    public async Task SetRole_changes_role()
    {
        // Promote the demo customer (ProfileId=1) to Administrator. The seed admin has
        // ProfileId=0, so (Administrator, 1) does not collide with the unique (Role, ProfileId) index.
        await using var db = CreateContext();
        var result = await new AccountService(db).SetRoleAsync(1003, "Administrator", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountOutcome.Success));
        Assert.That(result.Account!.Role, Is.EqualTo("Administrator"));
    }

    [Test]
    public async Task SetRole_with_invalid_role_returns_InvalidRole()
    {
        await using var db = CreateContext();
        var result = await new AccountService(db).SetRoleAsync(1003, "Overlord", CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(AccountOutcome.InvalidRole));
    }
}
