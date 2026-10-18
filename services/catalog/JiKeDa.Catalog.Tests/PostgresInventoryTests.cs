using JiKeDa.Catalog.Api.Data;
using JiKeDa.Catalog.Api.Features.Dishes;
using JiKeDa.Catalog.Api.Features.Inventory;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;

namespace JiKeDa.Catalog.Tests;

/// <summary>
/// Runs the real migrations and real row-locking on PostgreSQL. Set CATALOG_TEST_POSTGRES to a
/// server connection string (a throw-away database is created per test). CI also sets
/// REQUIRE_POSTGRES_TESTS=true so these tests fail instead of being skipped there.
/// </summary>
[NonParallelizable]
public sealed class PostgresInventoryTests
{
    private string _connectionString = null!;
    private string _database = null!;

    [SetUp]
    public async Task CreateDatabase()
    {
        var server = Environment.GetEnvironmentVariable("CATALOG_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_POSTGRES_TESTS") == "true")
                Assert.Fail("CATALOG_TEST_POSTGRES is not set but PostgreSQL tests are required.");
            Assert.Ignore("CATALOG_TEST_POSTGRES is not set.");
        }

        _database = "catalog_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        _connectionString = new NpgsqlConnectionStringBuilder(server) { Database = _database, Pooling = false }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync(); // the real migrations, from an empty database
    }

    [TearDown]
    public async Task DropDatabase()
    {
        if (_database is null) return;
        var server = Environment.GetEnvironmentVariable("CATALOG_TEST_POSTGRES")!;
        await using var admin = new NpgsqlConnection(server);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private CatalogDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<int> StockOf(int dishId)
    {
        await using var db = CreateContext();
        return (await db.Dishes.AsNoTracking().SingleAsync(d => d.Id == dishId)).Inventory;
    }

    private async Task SetStock(int dishId, int inventory)
    {
        await using var db = CreateContext();
        await db.Dishes.Where(d => d.Id == dishId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Inventory, inventory));
    }

    private async Task<ReserveResult> Reserve(string reservationId, int dishId, int quantity)
    {
        await using var db = CreateContext();
        return await new InventoryService(db, TimeProvider.System).ReserveAsync(
            reservationId, [new ReserveInventoryItem { DishId = dishId, Quantity = quantity }], CancellationToken.None);
    }

    [Test]
    public async Task Seeded_ids_do_not_collide_with_generated_ids()
    {
        await using var db = CreateContext();
        var dish = await new DishService(db, TimeProvider.System).CreateAsync(1,
            new CreateDishRequest { Name = "New Dish", Price = 9.90m, Inventory = 3 }, CancellationToken.None);

        Assert.That(await db.Dishes.CountAsync(), Is.EqualTo(7));
        Assert.That(dish!.Id, Is.GreaterThanOrEqualTo(CatalogDbContext.FirstGeneratedId));
    }

    [Test]
    public async Task Thirty_concurrent_checkouts_for_twenty_units_never_oversell()
    {
        await SetStock(1, 20);

        var results = await Task.WhenAll(Enumerable.Range(1, 30)
            .Select(i => Task.Run(() => Reserve($"race-{i}", 1, 1))));

        Assert.That(results.Count(r => r.Outcome == ReserveOutcome.Reserved), Is.EqualTo(20));
        Assert.That(results.Count(r => r.Outcome == ReserveOutcome.InsufficientStock), Is.EqualTo(10));
        Assert.That(await StockOf(1), Is.EqualTo(0));
    }

    [Test]
    public async Task Concurrent_replays_of_one_reservation_deduct_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(_ => Task.Run(() => Reserve("same-order", 1, 2))));

        Assert.That(results.Count(r => r.Outcome == ReserveOutcome.Reserved), Is.EqualTo(1));
        Assert.That(results.Count(r => r.Outcome == ReserveOutcome.Replayed), Is.EqualTo(9));
        Assert.That(await StockOf(1), Is.EqualTo(498));
    }

    [Test]
    public async Task Concurrent_releases_restore_once()
    {
        await Reserve("release-race", 1, 5);

        await Task.WhenAll(Enumerable.Range(1, 10).Select(_ => Task.Run(async () =>
        {
            await using var db = CreateContext();
            return await new InventoryService(db, TimeProvider.System).ReleaseAsync("release-race", null, CancellationToken.None);
        })));

        Assert.That(await StockOf(1), Is.EqualTo(500));
    }

    [Test]
    public async Task Database_rejects_negative_stock_even_without_the_service()
    {
        await using var db = CreateContext();
        var write = () => db.Dishes.Where(d => d.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(d => d.Inventory, -1));

        Assert.That(write, Throws.InstanceOf<PostgresException>()
            .With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.CheckViolation));
    }
}
