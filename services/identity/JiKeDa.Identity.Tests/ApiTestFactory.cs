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
