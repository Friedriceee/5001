using System.Text.Json;
using JiKeDa.Fulfilment.Api.Data;
using JiKeDa.Fulfilment.Api.Events;
using JiKeDa.Fulfilment.Api.Features;
using JiKeDa.Fulfilment.Api.Features.Delivery;
using JiKeDa.Fulfilment.Api.Features.Merchant;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using JiKeDa.Fulfilment.Api.Common;
using NUnit.Framework;

namespace JiKeDa.Fulfilment.Tests;

[TestFixture]
public sealed class FulfilmentFlowTests
{
    private SqliteConnection _connection = null!;
    private FulfilmentDbContext _db = null!;
    private FakeOrderEvents _events = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _db = new FulfilmentDbContext(new DbContextOptionsBuilder<FulfilmentDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
        if (!await _db.Riders.AnyAsync(rider => rider.Id == 1))
        {
            _db.Riders.Add(new Rider { Id = 1, Name = "Rider One", Zone = "ZoneA", Capacity = 3 });
            await _db.SaveChangesAsync();
        }
        _events = new FakeOrderEvents();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task Consumer_ProjectsOrderOnce_AndAdvancesCheckpoint()
    {
        _events.Messages.Add(Message(1, 42, [new(1, 7, "Noodles", 2, 12m)]));
        var consumer = new OrderEventConsumer(_db, _events, TimeProvider.System);

        Assert.That(await consumer.RunOnceAsync(CancellationToken.None), Is.EqualTo(1));
        Assert.That(await consumer.RunOnceAsync(CancellationToken.None), Is.EqualTo(0));

        var order = await _db.Orders.Include(o => o.MerchantFulfilments).SingleAsync();
        var checkpoint = await _db.EventCheckpoints.SingleAsync();
        Assert.Multiple((Action)(() =>
        {
            Assert.That(order.ExternalOrderId, Is.EqualTo(42));
            Assert.That(order.MerchantFulfilments, Has.Count.EqualTo(1));
            Assert.That(checkpoint.LastEventId, Is.EqualTo(1));
        }));
    }

    [Test]
    public async Task QueueConsumer_AcceptsOutOfOrderEvents_AndDeduplicatesRedelivery()
    {
        var consumer = new OrderEventConsumer(_db, _events, TimeProvider.System);
        var second = Message(20, 52, [new(1, 7, "Noodles", 1, 12m)]);
        var first = Message(10, 51, [new(1, 7, "Noodles", 1, 12m)]);
        await consumer.ConsumeQueueAsync(second, CancellationToken.None);
        await consumer.ConsumeQueueAsync(first, CancellationToken.None);
        await consumer.ConsumeQueueAsync(second, CancellationToken.None);
        Assert.Multiple((Action)(() =>
        {
            Assert.That(_db.Orders.Count(), Is.EqualTo(2));
            Assert.That(_db.ProcessedOrderEvents.Count(), Is.EqualTo(2));
            Assert.That(_db.EventCheckpoints.Count(), Is.Zero);
            Assert.That(_db.Orders.Select(o => o.ExternalOrderId), Is.EquivalentTo(new[] { 51, 52 }));
        }));
    }

    [Test]
    public async Task MerchantThenRider_ProgressesACompleteDelivery()
    {
        _events.Messages.Add(Message(1, 43, [new(1, 7, "Noodles", 1, 12m), new(2, 8, "Rice", 1, 10m)]));
        await new OrderEventConsumer(_db, _events, TimeProvider.System).RunOnceAsync(CancellationToken.None);
        var merchant = new MerchantFulfilmentService(_db, TimeProvider.System);
        foreach (var merchantId in new[] { 1, 2 })
        {
            Assert.That((await merchant.AdvanceAsync(43, merchantId, new MerchantActionRequest { Action = "Accept" }, CancellationToken.None)).Outcome, Is.EqualTo(MerchantActionOutcome.Success));
            Assert.That((await merchant.AdvanceAsync(43, merchantId, new MerchantActionRequest { Action = "StartPreparing" }, CancellationToken.None)).Outcome, Is.EqualTo(MerchantActionOutcome.Success));
            Assert.That((await merchant.AdvanceAsync(43, merchantId, new MerchantActionRequest { Action = "MarkReady" }, CancellationToken.None)).Outcome, Is.EqualTo(MerchantActionOutcome.Success));
        }
        var delivery = new DeliveryService(_db, TimeProvider.System);
        Assert.That((await delivery.AcceptAsync(43, 1, CancellationToken.None)).Outcome, Is.EqualTo(DeliveryOutcome.Success));
        foreach (var action in new[] { "PickUp", "StartDelivering", "Complete" })
            Assert.That((await delivery.AdvanceAsync(43, 1, new DeliveryActionRequest { Action = action }, CancellationToken.None)).Outcome, Is.EqualTo(DeliveryOutcome.Success));

        var order = await _db.Orders.SingleAsync(o => o.ExternalOrderId == 43);
        var rider = await _db.Riders.SingleAsync(r => r.Id == 1);
        var merchantOrders = await merchant.MineAsync(1, CancellationToken.None);
        Assert.Multiple((Action)(() =>
        {
            Assert.That(order.Status, Is.EqualTo(FulfilmentOrderStatus.Completed));
            Assert.That(rider.ActiveDeliveryCount, Is.Zero);
            Assert.That(merchantOrders.Single().Status, Is.EqualTo("ReadyForDelivery"));
            Assert.That(merchantOrders.Single().OverallStatus, Is.EqualTo("Completed"));
        }));
    }

    [Test]
    public async Task Internal_status_read_model_returns_the_latest_order_status()
    {
        _events.Messages.Add(Message(1, 44, [new(1, 7, "Noodles", 1, 12m)]));
        await new OrderEventConsumer(_db, _events, TimeProvider.System).RunOnceAsync(CancellationToken.None);
        var order = await _db.Orders.SingleAsync();
        order.Status = FulfilmentOrderStatus.Completed;
        await _db.SaveChangesAsync();

        var action = await new OrderStatusesController(_db).Get([44, 999], CancellationToken.None);
        var ok = (OkObjectResult)action.Result!;
        var envelope = (ApiResult<IReadOnlyList<FulfilmentOrderStatusResponse>>)ok.Value!;

        Assert.That(envelope.Data!.Single(), Is.EqualTo(new FulfilmentOrderStatusResponse(44, "Completed")));
    }

    private static OrderEvent Message(long eventId, int orderId, IReadOnlyList<OrderConfirmedLine> lines) => new(eventId, "OrderConfirmed",
        JsonSerializer.SerializeToElement(new OrderConfirmedEvent(orderId, 1, 1,
            new OrderConfirmedAddress("Demo Customer", "13800000000", "1 Campus Drive", "#01-01"),
            0, lines, 5m, lines.Sum(l => l.UnitPrice * l.Quantity) + 5m, DateTime.UtcNow, "test")));

    private sealed class FakeOrderEvents : IOrderEventClient
    {
        public List<OrderEvent> Messages { get; } = [];
        public Task<IReadOnlyList<OrderEvent>> GetAfterAsync(long after, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderEvent>>(Messages.Where(m => m.Id > after).ToList());
    }
}
