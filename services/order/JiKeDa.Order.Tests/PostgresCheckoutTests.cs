using System.Net;
using System.Net.Http.Json;
using JiKeDa.Order.Api.Data;
using Npgsql;
using NUnit.Framework;

namespace JiKeDa.Order.Tests;

/// <summary>
/// Concurrency on the real migrations and PostgreSQL. Set ORDER_TEST_POSTGRES to a server connection
/// string (a throw-away database is created per test). CI also sets REQUIRE_POSTGRES_TESTS=true.
/// </summary>
[NonParallelizable]
public sealed class PostgresCheckoutTests
{
    private string? _server;
    private string _database = null!;
    private ApiTestFactory _factory = null!;
    private HttpClient _customer = null!;

    [SetUp]
    public async Task SetUp()
    {
        _server = Environment.GetEnvironmentVariable("ORDER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(_server))
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_POSTGRES_TESTS") == "true")
                Assert.Fail("ORDER_TEST_POSTGRES is not set but PostgreSQL tests are required.");
            Assert.Ignore("ORDER_TEST_POSTGRES is not set.");
        }
        _database = "order_test_" + Guid.NewGuid().ToString("N")[..12];
        await Admin($"CREATE DATABASE \"{_database}\"");
        _factory = new ApiTestFactory(new NpgsqlConnectionStringBuilder(_server) { Database = _database }.ConnectionString);
        _factory.EnsureDatabase(); // real migrations from an empty database
        _customer = _factory.CreateClient("Customer", 1);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_server is null) return;
        _factory.Dispose();
        NpgsqlConnection.ClearAllPools();
        await Admin($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private async Task Admin(string sql)
    {
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static object Body(string? coupon = null) => new { addressId = 1, couponCode = coupon, needUtensils = 0, paymentMethod = "SimulatedCard" };

    [Test]
    public async Task Generated_ids_do_not_collide_with_seeded_rows()
    {
        var created = await _customer.PostAsJsonAsync("/api/addresses", new { address = "PGP House", contactName = "C One", phoneNumber = "81234567" });
        var id = (await created.ReadResultAsync<Api.Features.Cart.AddressResponse>()).Data!.Id;
        Assert.That(id, Is.GreaterThanOrEqualTo(OrderDbContext.FirstGeneratedId));
    }

    [Test]
    public async Task Ten_concurrent_retries_of_one_checkout_create_one_order()
    {
        await _customer.PostAsJsonAsync("/api/cart/items", new { dishId = 1, quantity = 2 });

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _customer.CheckoutAsync("key-concurrent-1", Body()))));

        var codes = responses.Select(r => r.StatusCode).ToList();
        Assert.That(codes.Count(c => c == HttpStatusCode.Created), Is.EqualTo(1));
        Assert.That(codes.All(c => c is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict), Is.True, string.Join(",", codes));
        Assert.That(_factory.WithDb(db => db.Orders.Count()), Is.EqualTo(1));
        Assert.That(_factory.Catalog.ReserveCalls, Is.EqualTo(1), "only the attempt owner reserves");
        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(498));
    }

    [Test]
    public async Task Concurrent_checkouts_with_different_keys_fight_for_one_cart_and_one_coupon()
    {
        // 8 x 12.00 = 96, minus the 50/-8 offer = 88, reaches SAVE12 (one use left). Eight different keys race.
        await _customer.PostAsJsonAsync("/api/cart/items", new { dishId = 1, quantity = 8 });

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => _customer.CheckoutAsync($"key-race-{i:D6}", Body("SAVE12")))));

        var outcomes = await Task.WhenAll(responses.Select(async r => $"{(int)r.StatusCode}:{await r.Content.ReadAsStringAsync()}"));
        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Created), Is.EqualTo(1), string.Join("\n", outcomes));
        Assert.That(_factory.WithDb(db => db.Orders.Count()), Is.EqualTo(1));
        Assert.That(_factory.WithDb(db => db.CustomerCoupons.Single(c => c.CouponId == 2).UsedQuantity), Is.EqualTo(1));
        Assert.That(_factory.Catalog.Stock(1), Is.EqualTo(492), "every losing reservation was released");
        Assert.That(_factory.WithDb(db => db.CheckoutAttempts.Count(a => a.Status == Api.Features.Checkout.CheckoutAttemptStatus.InProgress)), Is.Zero);
    }
}
