using System.ComponentModel.DataAnnotations;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Cart;

public sealed class AddCartItemRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "dishId must be greater than 0.")]
    public int DishId { get; set; }

    [Range(1, 999, ErrorMessage = "quantity must be between 1 and 999.")]
    public int Quantity { get; set; } = 1;
}

public sealed class UpdateCartItemRequest
{
    [Range(1, 999, ErrorMessage = "quantity must be between 1 and 999.")]
    public int Quantity { get; set; }
}

public sealed record CartItemResponse(int Id, int MerchantId, int DishId, string DishName, decimal UnitPrice, int Quantity, decimal LineTotal, string? ImageUrl, bool IsAvailable);
public sealed record CartMerchantGroupResponse(int MerchantId, string MerchantName, IReadOnlyList<CartItemResponse> Items, decimal Subtotal, decimal Discount, decimal Total);
public sealed record CartResponse(IReadOnlyList<CartMerchantGroupResponse> Merchants, int TotalCount, decimal Subtotal, decimal Discount, decimal Total);

public enum CartOutcome { Success, DishNotFound, CartItemNotFound, InsufficientStock, Unavailable }
public sealed record CartResult(CartOutcome Outcome, CartResponse? Cart = null, string? Message = null);

/// <summary>
/// Persistent cart, migrated from the monolith. The client sends only a dish id and a quantity;
/// merchant, name, price and offers are read from Catalog every time the cart is shown.
/// </summary>
public sealed class CartService
{
    private readonly OrderDbContext _db;
    private readonly ICatalogClient _catalog;
    private readonly MerchantDiscountStrategy _offers = new();
    private readonly TimeProvider _timeProvider;

    public CartService(OrderDbContext db, ICatalogClient catalog, TimeProvider timeProvider)
    {
        _db = db;
        _catalog = catalog;
        _timeProvider = timeProvider;
    }

    public Task<CartResponse> GetAsync(int customerId, CancellationToken cancellationToken) =>
        BuildAsync(customerId, cancellationToken);

    public async Task<CartResult> AddAsync(int customerId, AddCartItemRequest request, CancellationToken cancellationToken)
    {
        var dish = await _catalog.GetDishAsync(request.DishId, cancellationToken);
        if (dish is null)
            return new(CartOutcome.DishNotFound, Message: "Menu item was not found.");

        var item = await _db.CartItems.SingleOrDefaultAsync(c => c.CustomerId == customerId && c.DishId == dish.Id, cancellationToken);
        var quantity = (item?.Quantity ?? 0) + request.Quantity;
        var check = CheckStock(dish, quantity);
        if (check is not null) return check;

        if (item is null)
            _db.CartItems.Add(new CartItem { CustomerId = customerId, MerchantId = dish.MerchantId, DishId = dish.Id, Quantity = quantity });
        else
            item.Quantity = quantity;
        await _db.SaveChangesAsync(cancellationToken);
        return new(CartOutcome.Success, await BuildAsync(customerId, cancellationToken));
    }

    public async Task<CartResult> UpdateAsync(int customerId, int cartItemId, UpdateCartItemRequest request, CancellationToken cancellationToken)
    {
        var item = await _db.CartItems.SingleOrDefaultAsync(c => c.Id == cartItemId && c.CustomerId == customerId, cancellationToken);
        if (item is null)
            return new(CartOutcome.CartItemNotFound, Message: "Cart item was not found.");
        var dish = await _catalog.GetDishAsync(item.DishId, cancellationToken);
        if (dish is null)
            return new(CartOutcome.DishNotFound, Message: "Menu item was not found.");
        var check = CheckStock(dish, request.Quantity);
        if (check is not null) return check;

        item.Quantity = request.Quantity;
        await _db.SaveChangesAsync(cancellationToken);
        return new(CartOutcome.Success, await BuildAsync(customerId, cancellationToken));
    }

    public async Task<CartResult> RemoveAsync(int customerId, int cartItemId, CancellationToken cancellationToken)
    {
        var removed = await _db.CartItems.Where(c => c.Id == cartItemId && c.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        return removed == 0
            ? new(CartOutcome.CartItemNotFound, Message: "Cart item was not found.")
            : new(CartOutcome.Success, await BuildAsync(customerId, cancellationToken));
    }

    public async Task<CartResult> RemoveMerchantAsync(int customerId, int merchantId, CancellationToken cancellationToken)
    {
        var removed = await _db.CartItems.Where(c => c.CustomerId == customerId && c.MerchantId == merchantId).ExecuteDeleteAsync(cancellationToken);
        return removed == 0
            ? new(CartOutcome.CartItemNotFound, Message: "There are no items from this merchant in the cart.")
            : new(CartOutcome.Success, await BuildAsync(customerId, cancellationToken));
    }

    public async Task<CartResponse> ClearAsync(int customerId, CancellationToken cancellationToken)
    {
        await _db.CartItems.Where(c => c.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        return await BuildAsync(customerId, cancellationToken);
    }

    // Stock is checked here as a courtesy; the real guarantee is the Catalog reservation at checkout.
    private static CartResult? CheckStock(CatalogDish dish, int quantity)
    {
        if (!dish.IsAvailable && dish.Inventory > 0)
            return new(CartOutcome.Unavailable, Message: "This menu item is not currently available.");
        if (quantity > dish.Inventory)
            return new(CartOutcome.InsufficientStock, Message: $"Only {dish.Inventory} left of {dish.Name}.");
        return null;
    }

    private async Task<CartResponse> BuildAsync(int customerId, CancellationToken cancellationToken)
    {
        var items = await _db.CartItems.AsNoTracking().Where(c => c.CustomerId == customerId).OrderBy(c => c.Id).ToListAsync(cancellationToken);
        if (items.Count == 0)
            return new CartResponse([], 0, 0m, 0m, 0m);

        var now = _timeProvider.GetUtcNow();
        var groups = new List<CartMerchantGroupResponse>();
        foreach (var group in items.GroupBy(i => i.MerchantId).OrderBy(g => g.Key))
        {
            var merchant = await _catalog.GetMerchantAsync(group.Key, cancellationToken);
            var offers = await _catalog.GetOffersAsync(group.Key, cancellationToken);
            var lines = new List<CartItemResponse>();
            foreach (var item in group)
            {
                var dish = await _catalog.GetDishAsync(item.DishId, cancellationToken);
                lines.Add(dish is null
                    ? new CartItemResponse(item.Id, item.MerchantId, item.DishId, "(no longer available)", 0m, item.Quantity, 0m, null, false)
                    : new CartItemResponse(item.Id, item.MerchantId, item.DishId, dish.Name, dish.Price, item.Quantity,
                        Money.Round(dish.Price * item.Quantity), dish.ImageUrl, dish.IsAvailable && dish.Inventory >= item.Quantity));
            }
            var subtotal = Money.Round(lines.Sum(l => l.LineTotal));
            var discount = _offers.Apply(new DiscountContext(subtotal, now, offers)).Amount;
            groups.Add(new CartMerchantGroupResponse(group.Key, merchant?.Name ?? $"Merchant {group.Key}", lines, subtotal, discount, Money.Round(subtotal - discount)));
        }

        return new CartResponse(groups, items.Sum(i => i.Quantity),
            groups.Sum(g => g.Subtotal), groups.Sum(g => g.Discount), groups.Sum(g => g.Total));
    }
}
