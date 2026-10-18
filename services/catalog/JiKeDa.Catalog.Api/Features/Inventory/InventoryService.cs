using JiKeDa.Catalog.Api.Data;
using JiKeDa.Catalog.Api.Features.Menu;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Catalog.Api.Features.Inventory;

public enum ReserveOutcome { Reserved, Replayed, InvalidRequest, DishNotFound, Unavailable, InsufficientStock, Conflict }
public enum ReleaseOutcome { Released, AlreadyReleased, NotFound }

public sealed record ReserveResult(ReserveOutcome Outcome, ReservationResponse? Reservation = null, string? Message = null);
public sealed record ReleaseResult(ReleaseOutcome Outcome, ReleaseResponse? Release = null, string? Message = null);

/// <summary>
/// Stock reservation ledger, migrated from the monolith InventoryService. The reservation id
/// replaces the monolith's order id because Catalog cannot see the Order database.
/// All lines of one reservation succeed or none do; a replay with the same id and items
/// returns the stored reservation without deducting again.
/// </summary>
public sealed class InventoryService
{
    private readonly CatalogDbContext _db;
    private readonly TimeProvider _timeProvider;

    public InventoryService(CatalogDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<ReserveResult> ReserveAsync(
        string reservationId,
        IReadOnlyList<ReserveInventoryItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0 || items.Any(i => i.DishId <= 0 || i.Quantity <= 0))
            return new(ReserveOutcome.InvalidRequest, Message: "Positive dish ids and quantities are required.");

        (int DishId, int Quantity)[] requested;
        try
        {
            requested = items.GroupBy(i => i.DishId)
                .Select(g => (g.Key, checked((int)g.Sum(i => (long)i.Quantity))))
                .OrderBy(i => i.Key)
                .ToArray();
        }
        catch (OverflowException)
        {
            return new(ReserveOutcome.InvalidRequest, Message: "The requested quantity is too large.");
        }

        ReserveResult result;
        try
        {
            result = await ReserveInTransactionAsync(reservationId, requested, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent call with the same reservation id inserted its ledger first (primary key
            // violation). Our transaction has rolled back, so answer from the committed ledger.
            _db.ChangeTracker.Clear();
            var committed = await LedgerAsync(reservationId, cancellationToken);
            if (committed.Count == 0) throw;
            return CheckReplay(reservationId, committed, requested);
        }

        // A replay may have waited on the first call's row locks and then failed the stock
        // predicate. Once our partial changes are rolled back, check the committed ledger.
        if (result.Outcome == ReserveOutcome.InsufficientStock)
        {
            var committed = await LedgerAsync(reservationId, cancellationToken);
            if (committed.Count > 0) return CheckReplay(reservationId, committed, requested);
        }
        return result;
    }

    private async Task<ReserveResult> ReserveInTransactionAsync(
        string reservationId,
        IReadOnlyList<(int DishId, int Quantity)> requested,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await LedgerAsync(reservationId, cancellationToken);
        if (existing.Count > 0)
            return CheckReplay(reservationId, existing, requested);

        var now = _timeProvider.GetUtcNow();
        var lines = new List<InventoryReservation>();
        foreach (var (dishId, quantity) in requested)
        {
            var dish = await _db.Dishes.AsNoTracking().SingleOrDefaultAsync(d => d.Id == dishId, cancellationToken);
            if (dish is null)
                return await FailAsync(new(ReserveOutcome.DishNotFound, Message: $"Menu item {dishId} was not found."));
            if (!MenuItemFactory.FromDish(dish).IsAvailableAt(now))
                return await FailAsync(new(ReserveOutcome.Unavailable, Message: $"Menu item {dishId} is not currently available."));

            // Stock check and subtraction run as one UPDATE. PostgreSQL re-evaluates the
            // predicate after a competing row lock is released, so stock never goes negative.
            var changed = await _db.Dishes
                .Where(d => d.Id == dishId && d.Inventory >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Inventory, d => d.Inventory - quantity), cancellationToken);
            if (changed != 1)
                return await FailAsync(new(ReserveOutcome.InsufficientStock, Message: $"There is not enough stock for menu item {dishId}."));

            lines.Add(new InventoryReservation
            {
                ReservationId = reservationId,
                DishId = dishId,
                MerchantId = dish.MerchantId,
                DishName = dish.Name,
                UnitPrice = dish.Price,
                Quantity = quantity,
                ReservedAtUtc = now,
            });
        }

        _db.InventoryReservations.AddRange(lines);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ReserveOutcome.Reserved, ToResponse(reservationId, lines, replayed: false));

        async Task<ReserveResult> FailAsync(ReserveResult failure)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return failure;
        }
    }

    /// <summary>
    /// Restores stock for every unreleased line (optionally only one merchant's lines, for a
    /// merchant rejection). Releasing twice never restores twice.
    /// </summary>
    public async Task<ReleaseResult> ReleaseAsync(
        string reservationId,
        int? merchantId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var lines = (await LedgerAsync(reservationId, cancellationToken))
            .Where(l => merchantId is null || l.MerchantId == merchantId)
            .ToList();
        if (lines.Count == 0)
            return new(ReleaseOutcome.NotFound, Message: "No inventory reservation was found.");

        var now = _timeProvider.GetUtcNow();
        var released = 0;
        foreach (var line in lines.Where(l => !l.IsReleased))
        {
            // Claim the line first; a concurrent release that already claimed it changes 0 rows.
            var claimed = await _db.InventoryReservations
                .Where(r => r.ReservationId == reservationId && r.DishId == line.DishId && !r.IsReleased)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.IsReleased, true)
                    .SetProperty(r => r.ReleasedAtUtc, now), cancellationToken);
            if (claimed == 0) continue;

            await _db.Dishes
                .Where(d => d.Id == line.DishId && d.Inventory <= int.MaxValue - line.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Inventory, d => d.Inventory + line.Quantity), cancellationToken);
            released++;
        }

        await transaction.CommitAsync(cancellationToken);
        return new(
            released == 0 ? ReleaseOutcome.AlreadyReleased : ReleaseOutcome.Released,
            new ReleaseResponse(reservationId, released, released == 0));
    }

    private Task<List<InventoryReservation>> LedgerAsync(string reservationId, CancellationToken cancellationToken) =>
        _db.InventoryReservations.AsNoTracking()
            .Where(r => r.ReservationId == reservationId)
            .OrderBy(r => r.DishId)
            .ToListAsync(cancellationToken);

    private static ReserveResult CheckReplay(
        string reservationId,
        IReadOnlyList<InventoryReservation> existing,
        IReadOnlyList<(int DishId, int Quantity)> requested)
    {
        var same = existing.Count == requested.Count
            && existing.Zip(requested).All(pair => !pair.First.IsReleased
                && pair.First.DishId == pair.Second.DishId
                && pair.First.Quantity == pair.Second.Quantity);
        return same
            ? new(ReserveOutcome.Replayed, ToResponse(reservationId, existing, replayed: true))
            : new(ReserveOutcome.Conflict, Message: "This reservation id was already used for different items or has been released.");
    }

    private static ReservationResponse ToResponse(
        string reservationId,
        IEnumerable<InventoryReservation> lines,
        bool replayed)
    {
        var items = lines
            .OrderBy(l => l.DishId)
            .Select(l => new ReservedItemResponse(
                l.DishId, l.MerchantId, l.DishName, l.UnitPrice, l.Quantity, l.UnitPrice * l.Quantity))
            .ToList();
        return new(reservationId, items, items.Sum(i => i.LineTotal), replayed);
    }
}
