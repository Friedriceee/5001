using System.Net;
using System.Net.Http.Json;
using JiKeDa.Identity.Api.Features.Accounts;
using NUnit.Framework;

namespace JiKeDa.Identity.Tests;

/// <summary>
/// Endpoint integration tests over the full HTTP + JWT pipeline.
/// Covers login happy path, registration, and the authorization matrix (401/403) for /api/accounts.
/// </summary>
public class IdentityEndpointTests
{
    private ApiTestFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiTestFactory();
        _factory.EnsureDatabase();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Login_with_seed_admin_returns_token()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Passw0rd!123" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<LoginDto>>();
        Assert.That(body?.Success, Is.True);
        Assert.That(body?.Data?.Role, Is.EqualTo("Administrator"));
        Assert.That(body?.Data?.AccessToken, Is.Not.Empty);
    }

    [Test]
    public async Task Login_with_wrong_password_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Register_customer_returns_created()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/customer/register", new
        {
            username = "endpointuser",
            password = "SecurePass123",
            displayName = "Endpoint User",
            phoneNumber = "13900000001",
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task Register_merchant_creates_a_login_linked_to_the_catalog_profile()
    {
        using var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/merchant/register", new
        {
            username = "newmerchant", password = "SecurePass123", merchantName = "New Restaurant",
            merchantAddress = "1 Campus Drive", contact = "12345678", dishType = "Noodles",
            timeForOpenBusiness = 32400, timeForCloseBusiness = 75600, walletPassword = "Wallet123",
        });
        Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "newmerchant", password = "SecurePass123" });
        var body = await login.Content.ReadFromJsonAsync<ApiResponse<LoginDto>>();
        Assert.Multiple((Action)(() =>
        {
            Assert.That(body?.Data?.Role, Is.EqualTo("Merchant"));
            Assert.That(body?.Data?.ProfileId, Is.EqualTo(1000));
        }));
    }

    [Test]
    public async Task Register_rider_creates_a_login_linked_to_the_fulfilment_profile()
    {
        using var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/rider/register", new
        {
            username = "newrider", password = "SecurePass123", name = "New Rider",
            phoneNumber = "12345678", zone = "ZoneB", capacity = 4,
        });
        Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "newrider", password = "SecurePass123" });
        var body = await login.Content.ReadFromJsonAsync<ApiResponse<LoginDto>>();
        Assert.Multiple((Action)(() =>
        {
            Assert.That(body?.Data?.Role, Is.EqualTo("Rider"));
            Assert.That(body?.Data?.ProfileId, Is.EqualTo(2000));
        }));
    }

    [Test]
    public async Task Anonymous_request_to_accounts_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/accounts");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase(AccountRole.Customer)]
    [TestCase(AccountRole.Merchant)]
    [TestCase(AccountRole.Rider)]
    public async Task Non_admin_request_to_accounts_returns_403(AccountRole role)
    {
        using var client = _factory.CreateAuthenticatedClient(role, 1);
        var response = await client.GetAsync("/api/accounts");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Administrator_can_list_accounts()
    {
        using var client = _factory.CreateAuthenticatedClient(AccountRole.Administrator, 0);
        var response = await client.GetAsync("/api/accounts?role=Customer");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedDto<AccountDto>>>();
        Assert.That(body?.Success, Is.True);
        Assert.That(body?.Data, Is.Not.Null);
    }

    private sealed record ApiResponse<T>(bool Success, T? Data, string? Message);
    private sealed record LoginDto(string AccessToken, DateTime ExpiresAtUtc, int AccountId, string Role, int ProfileId);
    private sealed record PagedDto<T>(List<T> Items, int Page, int PageSize, int TotalCount);
    private sealed record AccountDto(int Id, string Username, string Role, int ProfileId, bool IsActive);
}
