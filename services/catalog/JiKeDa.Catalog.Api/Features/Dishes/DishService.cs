using JiKeDa.Catalog.Api.Data;
using JiKeDa.Catalog.Api.Features.Menu;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Catalog.Api.Features.Dishes;

public enum DishDeleteOutcome { Deleted, NotFound, HasReservations }

/// <summary>Menu queries and merchant dish management, migrated from the monolith DishService.</summary>
public sealed class DishService
{
    private readonly CatalogDbContext _db;
    private readonly TimeProvider _timeProvider;

    public DishService(CatalogDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>Returns null when the merchant does not exist.</summary>
    public async Task<IReadOnlyList<DishResponse>?> ListForMerchantAsync(
        int merchantId,
        CancellationToken cancellationToken)
    {
        if (!await _db.Merchants.AnyAsync(m => m.Id == merchantId, cancellationToken))
            return null;

        var dishes = await _db.Dishes
            .AsNoTracking()
            .Where(d => d.MerchantId == merchantId)
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);
        return dishes.Select(ToResponse).ToList();
    }

    public async Task<DishResponse?> GetAsync(int dishId, CancellationToken cancellationToken)
    {
        var dish = await _db.Dishes.AsNoTracking().SingleOrDefaultAsync(d => d.Id == dishId, cancellationToken);
        return dish is null ? null : ToResponse(dish);
    }

    /// <summary>Returns null when the merchant profile does not exist in Catalog.</summary>
    public async Task<DishResponse?> CreateAsync(
        int merchantId,
        CreateDishRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _db.Merchants.AnyAsync(m => m.Id == merchantId, cancellationToken))
            return null;

        var dish = new Dish
        {
            MerchantId = merchantId,
            Name = request.Name!.Trim(),
            Price = request.Price,
            Category = Clean(request.Category),
            ImageUrl = Clean(request.ImageUrl),
            Inventory = request.Inventory,
        };
        ApplyMenuItem(dish, MenuItemValidation.Create(request));

        _db.Dishes.Add(dish);
        await _db.SaveChangesAsync(cancellationToken);
        return ToResponse(dish);
    }

    /// <summary>Returns null when the dish does not exist or belongs to another merchant.</summary>
    public async Task<DishResponse?> UpdateAsync(
        int dishId,
        int merchantId,
        UpdateDishRequest request,
        CancellationToken cancellationToken)
    {
        var dish = await _db.Dishes.SingleOrDefaultAsync(
            d => d.Id == dishId && d.MerchantId == merchantId, cancellationToken);
        if (dish is null)
            return null;

        dish.Name = request.Name!.Trim();
        dish.Price = request.Price;
        dish.Category = Clean(request.Category);
        dish.ImageUrl = Clean(request.ImageUrl);
        dish.Inventory = request.Inventory;
        ApplyMenuItem(dish, MenuItemValidation.Create(request));

        await _db.SaveChangesAsync(cancellationToken);
        return ToResponse(dish);
    }

    public async Task<DishDeleteOutcome> DeleteAsync(
        int dishId,
        int merchantId,
        CancellationToken cancellationToken)
    {
        var dish = await _db.Dishes.SingleOrDefaultAsync(
            d => d.Id == dishId && d.MerchantId == merchantId, cancellationToken);
        if (dish is null)
            return DishDeleteOutcome.NotFound;

        // Reservation lines keep the dish id for audit; the foreign key also restricts this.
        if (await _db.InventoryReservations.AnyAsync(r => r.DishId == dishId, cancellationToken))
            return DishDeleteOutcome.HasReservations;

        _db.Dishes.Remove(dish);
        await _db.SaveChangesAsync(cancellationToken);
        return DishDeleteOutcome.Deleted;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ApplyMenuItem(Dish dish, MenuItem item)
    {
        dish.ItemType = item.Type;
        dish.BundleSize = item.Options.BundleSize;
        dish.PromotionStartsAtUtc = item.Options.PromotionStartsAtUtc;
        dish.PromotionEndsAtUtc = item.Options.PromotionEndsAtUtc;
    }

    private DishResponse ToResponse(Dish dish) => new(
        dish.Id,
        dish.MerchantId,
        dish.Name,
        dish.Price,
        dish.Category,
        dish.ImageUrl,
        dish.Inventory,
        dish.ItemType.ToString(),
        dish.BundleSize,
        dish.PromotionStartsAtUtc,
        dish.PromotionEndsAtUtc,
        dish.Inventory > 0 && MenuItemFactory.FromDish(dish).IsAvailableAt(_timeProvider.GetUtcNow()));
}
