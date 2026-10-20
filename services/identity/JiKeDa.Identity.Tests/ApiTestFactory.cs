using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using JiKeDa.Identity.Api.Data;
using JiKeDa.Identity.Api.Features.Accounts;
using JiKeDa.Identity.Api.Features.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace JiKeDa.Identity.Tests;

/// <summary>
/// Spins up the Identity API with an in-memory SQLite database and a fixed JWT key,
/// so endpoint tests run the full HTTP + auth pipeline without PostgreSQL.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-jwt-signing-key-with-at-least-32-bytes";
    public const string Issuer = "JiKeDa";
    public const string Audience = "JiKeDa.Client";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public FakeMerchantProfiles Merchants { get; } = new();
    public FakeRiderProfiles Riders { get; } = new();

    public ApiTestFactory()
    {
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            var descriptor = services.Single(
                s => s.ServiceType == typeof(DbContextOptions<IdentityDbContext>));
            services.Remove(descriptor);
            services.AddDbContext<IdentityDbContext>(options => options.UseSqlite(_connection));
            foreach (var item in services.Where(item => item.ServiceType == typeof(IMerchantProfileClient) || item.ServiceType == typeof(IRiderProfileClient)).ToList())
                services.Remove(item);
            services.AddSingleton<IMerchantProfileClient>(Merchants);
            services.AddSingleton<IRiderProfileClient>(Riders);
        });
    }

    public void EnsureDatabase()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.Database.EnsureCreated();
    }

    public HttpClient CreateAuthenticatedClient(AccountRole role, int profileId, int accountId = 500)
    {
        var client = CreateClient();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, accountId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, accountId.ToString()),
            new Claim(ClaimTypes.Name, $"{role}-{accountId}"),
            new Claim("role", role.ToString()),
            new Claim(ClaimsPrincipalExtensions.ProfileIdClaim, profileId.ToString()),
        };
        var token = new JwtSecurityToken(
            Issuer, Audience, claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class FakeMerchantProfiles : IMerchantProfileClient
{
    private int _nextId = 999;
    public List<int> Deleted { get; } = [];
    public Task<int> CreateAsync(RegisterMerchantRequest request, CancellationToken cancellationToken) => Task.FromResult(Interlocked.Increment(ref _nextId));
    public Task DeleteAsync(int id, CancellationToken cancellationToken) { Deleted.Add(id); return Task.CompletedTask; }
}

public sealed class FakeRiderProfiles : IRiderProfileClient
{
    private int _nextId = 1999;
    public List<int> Deleted { get; } = [];
    public Task<int> CreateAsync(RegisterRiderRequest request, CancellationToken cancellationToken) => Task.FromResult(Interlocked.Increment(ref _nextId));
    public Task DeleteAsync(int id, CancellationToken cancellationToken) { Deleted.Add(id); return Task.CompletedTask; }
}
