using JiKeDa.Catalog.Api.Features.Dishes;
using JiKeDa.Catalog.Api.Features.Inventory;
using JiKeDa.Catalog.Api.Features.Menu;
using NUnit.Framework;

namespace JiKeDa.Catalog.Tests;

public sealed class InventoryServiceTests : ServiceTestBase
{
    // Seed: dish 1 (merchant 1, 12.00), dish 2 (merchant 1, 15.00), dish 4 (merchant 2, 16.00); 500 units each.
    private InventoryService CreateService(out Api.Data.CatalogDbContext db)
    {
        db = CreateContext();
        return new InventoryService(db, Clock);
    }

    private static ReserveInventoryItem Item(int dishId, int quantity) => new() { DishId = dishId, Quantity = quantity };

    [Test]
    public async Task Reserve_deducts_stock_and_returns_snapshot_prices()
    {
        var service = CreateService(out var db);
        await using var _ = db;

        var result = await service.ReserveAsync("checkout-1", [Item(1, 2), Item(4, 1)], CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ReserveOutcome.Reserved));
        Assert.That(result.Reservation!.TotalAmount, Is.EqualTo(40.00m));
        Assert.That(result.Reservation.Items.Select(i => (i.DishId, i.MerchantId, i.UnitPrice)),
            Is.EqualTo(new[] { (1, 1, 12.00m), (4, 2, 16.00m) }));
        Assert.That(StockOf(1), Is.EqualTo(498));
        Assert.That(StockOf(4), Is.EqualTo(499));
    }

    [Test]
    public async Task Duplicate_lines_for_one_dish_are_merged()
    {
        var service = CreateService(out var db);
        await using var _ = db;

        var result = await service.ReserveAsync("checkout-merge", [Item(1, 2), Item(1, 3)], CancellationToken.None);

        Assert.That(result.Reservation!.Items.Single().Quantity, Is.EqualTo(5));
        Assert.That(StockOf(1), Is.EqualTo(495));
    }

    [Test]
    public async Task Same_id_and_items_is_replayed_without_deducting_again()
    {
        var service = CreateService(out var db);
        await using var _ = db;
        await service.ReserveAsync("checkout-2", [Item(1, 2)], CancellationToken.None);

        var replay = await service.ReserveAsync("checkout-2", [Item(1, 2)], CancellationToken.None);

        Assert.That(replay.Outcome, Is.EqualTo(ReserveOutcome.Replayed));
        Assert.That(replay.Reservation!.Replayed, Is.True);
        Assert.That(replay.Reservation.TotalAmount, Is.EqualTo(24.00m));
        Assert.That(StockOf(1), Is.EqualTo(498));
    }

    [Test]
    public async Task Same_id_with_different_items_is_a_conflict()
    {
        var service = CreateService(out var db);
        await using var _ = db;
        await service.ReserveAsync("checkout-3", [Item(1, 2)], CancellationToken.None);

        var result = await service.ReserveAsync("checkout-3", [Item(1, 3)], CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ReserveOutcome.Conflict));
        Assert.That(StockOf(1), Is.EqualTo(498));
    }

    [Test]
    public async Task Insufficient_stock_on_any_line_rolls_back_every_line()
    {
        var service = CreateService(out var db);
        await using var _ = db;

        var result = await service.ReserveAsync("checkout-4", [Item(1, 2), Item(2, 501)], CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ReserveOutcome.InsufficientStock));
        Assert.That(StockOf(1), Is.EqualTo(500), "dish 1 must be restored when dish 2 fails");
        Assert.That(StockOf(2), Is.EqualTo(500));
        Assert.That(db.InventoryReservations.Any(), Is.False);
    }

    [Test]
    public async Task Unknown_dish_is_rejected_without_side_effects()
    {
        var service = CreateService(out var db);
        await using var _ = db;

        var result = await service.ReserveAsync("checkout-5", [Item(1, 1), Item(999, 1)], CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ReserveOutcome.DishNotFound));
        Assert.That(StockOf(1), Is.EqualTo(500));
    }

    [Test]
    public async Task Promotional_item_outside_its_window_cannot_be_reserved()
    {
        await using (var setup = CreateContext())
        {
            var promo = await setup.Dishes.FindAsync(3);
            promo!.ItemType = MenuItemType.Promotional;
            promo.PromotionStartsAtUtc = Clock.Now.AddHours(1);
            promo.PromotionEndsAtUtc = Clock.Now.AddHours(2);
            await setup.SaveChangesAsync();
        }
        var service = CreateService(out var db);
        await using var _ = db;

        var early = await service.ReserveAsync("promo-early", [Item(3, 1)], CancellationToken.None);
        Clock.Now = Clock.Now.AddMinutes(90);
        var during = await service.ReserveAsync("promo-during", [Item(3, 1)], CancellationToken.None);

        Assert.That(early.Outcome, Is.EqualTo(ReserveOutcome.Unavailable));
        Assert.That(during.Outcome, Is.EqualTo(ReserveOutcome.Reserved));
    }

    [Test]
    public async Task Release_restores_stock_once()
    {
        var service = CreateService(out var db);
        await using var _ = db;
        await service.ReserveAsync("checkout-6", [Item(1, 2), Item(4, 3)], CancellationToken.None);

        var first = await service.ReleaseAsync("checkout-6", null, CancellationToken.None);
        var second = await service.ReleaseAsync("checkout-6", null, CancellationToken.None);

        Assert.That(first.Outcome, Is.EqualTo(ReleaseOutcome.Released));
        Assert.That(first.Release!.ReleasedLines, Is.EqualTo(2));
        Assert.That(second.Outcome, Is.EqualTo(ReleaseOutcome.AlreadyReleased));
        Assert.That(StockOf(1), Is.EqualTo(500));
        Assert.That(StockOf(4), Is.EqualTo(500));
    }

    [Test]
    public async Task Release_can_be_limited_to_one_merchant()
    {
        var service = CreateService(out var db);
        await using var _ = db;
        await service.ReserveAsync("checkout-7", [Item(1, 2), Item(4, 3)], CancellationToken.None);

        var result = await service.ReleaseAsync("checkout-7", merchantId: 2, CancellationToken.None);

        Assert.That(result.Release!.ReleasedLines, Is.EqualTo(1));
        Assert.That(StockOf(1), Is.EqualTo(498), "merchant 1's line stays reserved");
        Assert.That(StockOf(4), Is.EqualTo(500));
    }

    [Test]
    public async Task Release_of_unknown_reservation_is_not_found()
    {
        var service = CreateService(out var db);
        await using var _ = db;

        var result = await service.ReleaseAsync("missing", null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(ReleaseOutcome.NotFound));
    }

    [Test]
    public async Task A_released_id_cannot_be_reserved_again()
    {
        var service = CreateService(out var db);
        await using var _ = db;
        await service.ReserveAsync("checkout-8", [Item(1, 2)], CancellationToken.None);
        await service.ReleaseAsync("checkout-8", null, CancellationToken.None);

        var again = await service.ReserveAsync("checkout-8", [Item(1, 2)], CancellationToken.None);

        Assert.That(again.Outcome, Is.EqualTo(ReserveOutcome.Conflict));
        Assert.That(StockOf(1), Is.EqualTo(500));
    }

    [Test]
    public async Task Dish_with_reservation_history_cannot_be_deleted()
    {
        var service = CreateService(out var db);
        await using var _ = db;
        await service.ReserveAsync("checkout-9", [Item(1, 1)], CancellationToken.None);

        var dishes = new DishService(db, Clock);
        var outcome = await dishes.DeleteAsync(1, merchantId: 1, CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(DishDeleteOutcome.HasReservations));
    }
}
