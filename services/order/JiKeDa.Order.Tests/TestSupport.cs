using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace JiKeDa.Order.Tests;

/// <summary>
/// In-memory Catalog with the same reservation rules as the real service (all-or-nothing, a replay
/// with the same id and lines does not deduct again, release restores once). Seed matches catalog_db.
/// </summary>
public sealed class FakeCatalog : ICatalogClient
{
    private readonly object _gate = new();
    private readonly Dictionary<int, CatalogDish> _dishes = new()
    {
        [1] = new(1, 1, "Braised Noodles", 12.00m, null, 500, true),
        [2] = new(2, 1, "Chicken Rice Bowl", 15.00m, null, 500, true),
        [4] = new(4, 2, "Sichuan Beef Noodles", 16.00m, null, 500, true),
    };
    private readonly Dictionary<string, (List<ReservedLine> Lines, bool Released)> _reservations = new();

    public bool Down { get; set; }
    public bool FailRelease { get; set; }
    public int ReserveCalls;
    public ConcurrentBag<string> Released { get; } = new();

    public int Stock(int dishId) { lock (_gate) return _dishes[dishId].Inventory; }
    public void SetStock(int dishId, int inventory) { lock (_gate) _dishes[dishId] = _dishes[dishId] with { Inventory = inventory }; }

    public Task<CatalogDish?> GetDishAsync(int dishId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        lock (_gate) return Task.FromResult(_dishes.TryGetValue(dishId, out var d) ? d with { IsAvailable = d.Inventory > 0 } : null);
    }

    public Task<CatalogMerchant?> GetMerchantAsync(int merchantId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return Task.FromResult<CatalogMerchant?>(merchantId is 1 or 2 ? new(merchantId, merchantId == 1 ? "Demo Merchant" : "Sprint 3 Merchant") : null);
    }

    public Task<IReadOnlyList<CatalogOffer>> GetOffersAsync(int merchantId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        IReadOnlyList<CatalogOffer> offers = merchantId switch
        {
            1 => [new(1, 1, 50m, 8m)],
            2 => [new(2, 2, 40m, 5m)],
            _ => [],
        };
        return Task.FromResult(offers);
    }

    public Task<ReserveResult> ReserveAsync(string reservationId, IReadOnlyList<ReservationLine> lines, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        Interlocked.Increment(ref ReserveCalls);
        lock (_gate)
        {
            var merged = lines.GroupBy(l => l.DishId).Select(g => new ReservationLine(g.Key, g.Sum(l => l.Quantity))).OrderBy(l => l.DishId).ToList();
            if (_reservations.TryGetValue(reservationId, out var existing))
            {
                var same = !existing.Released && existing.Lines.Select(l => (l.DishId, l.Quantity)).SequenceEqual(merged.Select(l => (l.DishId, l.Quantity)));
                return Task.FromResult(same
                    ? new ReserveResult(ReserveStatus.Reserved, ToReservation(reservationId, existing.Lines, replayed: true))
                    : new ReserveResult(ReserveStatus.Conflict, Message: "conflict"));
            }
            foreach (var line in merged)
            {
                if (!_dishes.TryGetValue(line.DishId, out var dish))
                    return Task.FromResult(new ReserveResult(ReserveStatus.DishNotFound, Message: $"Menu item {line.DishId} was not found."));
                if (dish.Inventory < line.Quantity)
                    return Task.FromResult(new ReserveResult(ReserveStatus.InsufficientStock, Message: $"There is not enough stock for menu item {line.DishId}."));
            }
            var reserved = merged.Select(l =>
            {
                var dish = _dishes[l.DishId];
                _dishes[l.DishId] = dish with { Inventory = dish.Inventory - l.Quantity };
                return new ReservedLine(dish.Id, dish.MerchantId, dish.Name, dish.Price, l.Quantity, dish.Price * l.Quantity);
            }).ToList();
            _reservations[reservationId] = (reserved, false);
            return Task.FromResult(new ReserveResult(ReserveStatus.Reserved, ToReservation(reservationId, reserved, replayed: false)));
        }
    }

    public Task<bool> ReleaseAsync(string reservationId, CancellationToken cancellationToken)
    {
        if (Down || FailRelease) throw new CatalogUnavailableException("fake catalog release failure");
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservationId, out var r)) return Task.FromResult(false);
            if (!r.Released)
            {
                foreach (var line in r.Lines)
                    _dishes[line.DishId] = _dishes[line.DishId] with { Inventory = _dishes[line.DishId].Inventory + line.Quantity };
                _reservations[reservationId] = (r.Lines, true);
                Released.Add(reservationId);
            }
            return Task.FromResult(true);
        }
    }

    private void ThrowIfDown()
    {
        if (Down) throw new CatalogUnavailableException("fake catalog is down");
    }

    private static Reservation ToReservation(string id, List<ReservedLine> lines, bool replayed) =>
        new(id, lines, lines.Sum(l => l.LineTotal), replayed);
}

/// <summary>Runs the Order API in memory with the fake Catalog, on SQLite or (when given) PostgreSQL.</summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-jwt-signing-key-with-at-least-32-bytes";
    private readonly SqliteConnection? _sqlite;
    private readonly string? _postgres;

    public FakeCatalog Catalog { get; } = new();

    public ApiTestFactory(string? postgresConnectionString = null)
    {
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("Outbox__Enabled", "false"); // tests drive the worker directly
        _postgres = postgresConnectionString;
        if (_postgres is null)
        {
            _sqlite = new SqliteConnection("Data Source=:memory:");
            _sqlite.Open();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.Remove(services.Single(s => s.ServiceType == typeof(DbContextOptions<OrderDbContext>)));
            services.AddDbContext<OrderDbContext>(options =>
            {
                if (_postgres is null) options.UseSqlite(_sqlite!);
                else options.UseNpgsql(_postgres);
            });
            services.RemoveAll<ICatalogClient>();
            services.AddSingleton<ICatalogClient>(Catalog);
        });
    }

    public void EnsureDatabase()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        if (_postgres is null) db.Database.EnsureCreated();
        else db.Database.Migrate();
    }

    public T WithDb<T>(Func<OrderDbContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<OrderDbContext>());
    }

    public HttpClient CreateClient(string role, int profileId)
    {
        var client = CreateClient();
        var token = new JwtSecurityToken(
            "JiKeDa", "JiKeDa.Client",
            [new Claim(JwtRegisteredClaimNames.Sub, "500"), new Claim("role", role), new Claim(ClaimsPrincipalExtensions.ProfileIdClaim, profileId.ToString())],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sqlite?.Dispose();
        base.Dispose(disposing);
    }
}

public static class HttpTestExtensions
{
    public static async Task<ApiResult<T>> ReadResultAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResult<T>>())!;

    public static Task<HttpResponseMessage> CheckoutAsync(this HttpClient client, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(s => s.ServiceType == typeof(T)).ToList())
            services.Remove(descriptor);
    }
}
