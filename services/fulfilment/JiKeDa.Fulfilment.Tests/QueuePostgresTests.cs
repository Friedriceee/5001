using System.Text.Json;
using JiKeDa.Fulfilment.Api.Data;
using JiKeDa.Fulfilment.Api.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NUnit.Framework;

namespace JiKeDa.Fulfilment.Tests;

[TestFixture, NonParallelizable]
public sealed class QueuePostgresTests
{
    private string _admin = null!;
    private string _connection = null!;
    private string _database = null!;

    [SetUp]
    public async Task SetUp()
    {
        _admin = Environment.GetEnvironmentVariable("ORDER_TEST_POSTGRES")!;
        if (string.IsNullOrWhiteSpace(_admin))
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_POSTGRES_TESTS") == "true")
                Assert.Fail("A real PostgreSQL connection is required for concurrent queue consumption tests.");
            Assert.Ignore("Set ORDER_TEST_POSTGRES to run PostgreSQL queue consumption tests.");
        }
        _database = "fulfilment_queue_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin).ExecuteNonQueryAsync();
        _connection = new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ConnectionString;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is null) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
    }

    private FulfilmentDbContext CreateContext() => new(new DbContextOptionsBuilder<FulfilmentDbContext>().UseNpgsql(_connection).Options);

    [Test]
    public async Task ConcurrentRedelivery_CommitsOneProjectionAndOneInboxReceipt()
    {
        var message = Message(1, 700);
        async Task Consume()
        {
            await using var db = CreateContext();
            await new OrderEventConsumer(db, new Feed(), TimeProvider.System).ConsumeQueueAsync(message, CancellationToken.None);
        }
        await Task.WhenAll(Consume(), Consume());
        await using var check = CreateContext();
        Assert.Multiple((Action)(() =>
        {
            Assert.That(check.Orders.Count(), Is.EqualTo(1));
            Assert.That(check.ProcessedOrderEvents.Count(), Is.EqualTo(1));
            Assert.That(check.MerchantFulfilments.Count(), Is.EqualTo(1));
        }));
    }

    [Test]
    public async Task AdditiveInboxMigration_PreservesAnExistingOrder()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20261018130000_AddDeliveryAddressSnapshot");
        var consumer = new OrderEventConsumer(db, new Feed(Message(1, 701)), TimeProvider.System);
        await consumer.RunOnceAsync(CancellationToken.None);
        await db.GetService<IMigrator>().MigrateAsync();
        Assert.That((await db.Orders.SingleAsync()).ExternalOrderId, Is.EqualTo(701));
        Assert.That(await db.ProcessedOrderEvents.CountAsync(), Is.Zero);
        await consumer.ConsumeQueueAsync(Message(1, 701), CancellationToken.None);
        Assert.That(await db.Orders.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.ProcessedOrderEvents.CountAsync(), Is.EqualTo(1));
    }

    private static OrderEvent Message(long id, int orderId) => new(id, "OrderConfirmed",
        JsonSerializer.SerializeToElement(new OrderConfirmedEvent(orderId, 1, 1, null, 0,
            [new(1, 7, "Noodles", 1, 12m)], 5m, 17m, DateTime.UtcNow, "queue-postgres-test")));

    private sealed class Feed(params OrderEvent[] messages) : IOrderEventClient
    {
        public Task<IReadOnlyList<OrderEvent>> GetAfterAsync(long after, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderEvent>>(messages.Where(m => m.Id > after).ToArray());
    }
}
