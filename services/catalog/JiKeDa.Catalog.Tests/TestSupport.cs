using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using JiKeDa.Catalog.Api.Common;
using JiKeDa.Catalog.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace JiKeDa.Catalog.Tests;

/// <summary>A clock tests can move, for promotional availability windows.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Base class for service tests: a fresh SQLite in-memory database per test,
/// built from the model (including the seeded merchants, dishes and offers).
/// </summary>
public abstract class ServiceTestBase
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<CatalogDbContext> _options = null!;
    protected TestClock Clock { get; private set; } = null!;

    [SetUp]
    public void SetUpDatabase()
    {
        Clock = new TestClock();
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_connection).Options;
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDownDatabase() => _connection.Dispose();

    protected CatalogDbContext CreateContext() => new(_options);

    protected int StockOf(int dishId)
    {
        using var db = CreateContext();
        return db.Dishes.AsNoTracking().Single(d => d.Id == dishId).Inventory;
    }
}

/// <summary>Runs the Catalog API in memory against SQLite with a fixed JWT key.</summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-jwt-signing-key-with-at-least-32-bytes";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

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
            services.Remove(services.Single(s => s.ServiceType == typeof(DbContextOptions<CatalogDbContext>)));
            services.AddDbContext<CatalogDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public void EnsureDatabase()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreated();
    }

    public HttpClient CreateClient(string role, int profileId)
    {
        var client = CreateClient();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "500"),
            new Claim(ClaimTypes.Name, $"{role}-{profileId}"),
            new Claim("role", role),
            new Claim(ClaimsPrincipalExtensions.ProfileIdClaim, profileId.ToString()),
        };
        var token = new JwtSecurityToken(
            "JiKeDa", "JiKeDa.Client", claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }
}

public static class HttpTestExtensions
{
    public static async Task<ApiResult<T>> ReadResultAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResult<T>>())!;
}
