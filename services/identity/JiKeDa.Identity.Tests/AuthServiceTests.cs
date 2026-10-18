using JiKeDa.Identity.Api.Data;
using JiKeDa.Identity.Api.Features.Accounts;
using JiKeDa.Identity.Api.Features.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace JiKeDa.Identity.Tests;

/// <summary>Unit tests for AuthService: registration, login, JWT claims, disabled-account rejection.</summary>
public class AuthServiceTests : ServiceTestBase
{
    private const string SeedPassword = "Passw0rd!123";

    private static AuthService NewService(IdentityDbContext db)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "JiKeDa",
            Audience = "JiKeDa.Client",
            Key = "test-only-jwt-signing-key-with-at-least-32-bytes",
            ExpiryMinutes = 60,
        });
        return new AuthService(db, new PasswordHasher<Account>(), options, TimeProvider.System);
    }

    private static RegisterCustomerRequest NewRegister(string username = "newuser") => new()
    {
        Username = username,
        Password = "SecurePass123",
        DisplayName = "New User",
        PhoneNumber = "13812345678",
    };

    [Test]
    public async Task RegisterCustomer_creates_account_and_profile()
    {
        await using var db = CreateContext();
        var (conflict, customer) = await NewService(db).RegisterCustomerAsync(NewRegister(), CancellationToken.None);

        Assert.That(conflict, Is.False);
        Assert.That(customer, Is.Not.Null);
        Assert.That(customer!.Username, Is.EqualTo("newuser"));

        await using var verify = CreateContext();
        Assert.That(verify.Accounts.Any(a => a.NormalizedUsername == "NEWUSER"), Is.True);
    }

    [Test]
    public async Task RegisterCustomer_rejects_case_insensitive_duplicate()
    {
        await using (var seed = CreateContext())
            await NewService(seed).RegisterCustomerAsync(NewRegister("Dup"), CancellationToken.None);

        await using var db = CreateContext();
        var (conflict, _) = await NewService(db).RegisterCustomerAsync(NewRegister(" dup "), CancellationToken.None);
        Assert.That(conflict, Is.True);
    }

    [Test]
    public async Task Login_with_valid_credentials_returns_token_and_claims()
    {
        await using var db = CreateContext();
        var login = await NewService(db).LoginAsync(
            new LoginRequest { Username = "admin", Password = SeedPassword },
            CancellationToken.None);

        Assert.That(login, Is.Not.Null);
        Assert.That(login!.Role, Is.EqualTo("Administrator"));
        Assert.That(login.AccessToken, Is.Not.Empty);
    }

    [Test]
    public async Task Login_with_wrong_password_returns_null()
    {
        await using var db = CreateContext();
        var login = await NewService(db).LoginAsync(
            new LoginRequest { Username = "admin", Password = "wrong" },
            CancellationToken.None);
        Assert.That(login, Is.Null);
    }

    [Test]
    public async Task Login_rejects_disabled_account()
    {
        await using (var mutate = CreateContext())
        {
            var account = mutate.Accounts.Single(a => a.NormalizedUsername == "CUSTOMER1");
            account.IsActive = false;
            mutate.SaveChanges();
        }

        await using var db = CreateContext();
        var login = await NewService(db).LoginAsync(
            new LoginRequest { Username = "customer1", Password = SeedPassword },
            CancellationToken.None);
        Assert.That(login, Is.Null);
    }
}
