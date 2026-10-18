using System.ComponentModel.DataAnnotations;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Pricing;

// ---- strategies (ported from the monolith; merchant offers now come from Catalog) ----

public enum DiscountStrategyKind { MerchantOffer, PlatformCoupon }

public enum PricingErrorCode
{
    EmptyCart, CouponNotFound, CouponInactive, CouponNotStarted, CouponExpired,
    CouponNotOwned, CouponAlreadyUsed, MinimumSpendNotMet, ItemUnavailable,
}

public sealed record DiscountContext(
    decimal Subtotal,
    DateTimeOffset Now,
    IReadOnlyList<CatalogOffer>? MerchantOffers = null,
    PlatformCoupon? Coupon = null);

public sealed record DiscountDecision(decimal Amount, PricingErrorCode? ErrorCode = null, string? Message = null)
{
    public bool IsValid => ErrorCode is null;
}

public interface IDiscountStrategy
{
    DiscountStrategyKind Kind { get; }
    DiscountDecision Apply(DiscountContext context);
}

/// <summary>The best merchant spend-threshold offer the subtotal reaches.</summary>
public sealed class MerchantDiscountStrategy : IDiscountStrategy
{
    public DiscountStrategyKind Kind => DiscountStrategyKind.MerchantOffer;

    public DiscountDecision Apply(DiscountContext context)
    {
        var subtotal = Money.Round(Math.Max(0m, context.Subtotal));
        var discount = context.MerchantOffers?
            .Where(o => o.MinPrice <= subtotal)
            .Select(o => o.AmountRemission)
            .DefaultIfEmpty(0m)
            .Max() ?? 0m;
        return new DiscountDecision(Money.Round(Math.Min(Math.Max(0m, discount), subtotal)));
    }
}

public sealed class PlatformCouponDiscountStrategy : IDiscountStrategy
{
    public DiscountStrategyKind Kind => DiscountStrategyKind.PlatformCoupon;

    public DiscountDecision Apply(DiscountContext context)
    {
        var coupon = context.Coupon ?? throw new ArgumentException("A platform coupon is required.", nameof(context));
        var subtotal = Money.Round(Math.Max(0m, context.Subtotal));
        if (!coupon.IsActive) return Invalid(PricingErrorCode.CouponInactive, "This coupon is not active.");
        if (context.Now < coupon.StartsAtUtc) return Invalid(PricingErrorCode.CouponNotStarted, "This coupon is not available yet.");
        if (context.Now >= coupon.ExpiresAtUtc) return Invalid(PricingErrorCode.CouponExpired, "This coupon has expired.");
        if (subtotal < coupon.MinimumSpend)
            return Invalid(PricingErrorCode.MinimumSpendNotMet,
                $"Spend at least ¥{coupon.MinimumSpend:0.00} after restaurant discounts to use this coupon.");
        return new DiscountDecision(Money.Round(Math.Min(coupon.DiscountAmount, subtotal)));
    }

    private static DiscountDecision Invalid(PricingErrorCode code, string message) => new(0m, code, message);
}

// ---- contracts ----

public sealed class PricingQuoteRequest
{
    [StringLength(40)]
    public string? CouponCode { get; set; }
}

public sealed record CouponOptionResponse(int Id, string Code, string Name, decimal MinimumSpend, decimal DiscountAmount, DateTimeOffset ExpiresAtUtc, int RemainingQuantity);
public sealed record MerchantPricingResponse(int MerchantId, string MerchantName, decimal Subtotal, decimal MerchantDiscount, decimal TotalAfterMerchantDiscount);
public sealed record PricingResult(
    decimal Subtotal,
    decimal MerchantDiscount,
    decimal CouponDiscount,
    decimal DeliveryFee,
    decimal Total,
    CouponOptionResponse? SelectedCoupon,
    IReadOnlyList<MerchantPricingResponse> Merchants);
public sealed record PricingOutcome(PricingResult? Pricing, PricingErrorCode? ErrorCode = null, string? Message = null)
{
    public bool Succeeded => Pricing is not null;
}

/// <summary>One priced line: the price comes from Catalog (live dish or reservation snapshot), never the client.</summary>
public sealed record PricedLine(int MerchantId, int DishId, decimal UnitPrice, int Quantity);

// ---- service ----

public sealed class PricingService
{
    public const decimal DefaultDeliveryFee = 5m;

    private readonly OrderDbContext _db;
    private readonly ICatalogClient _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<DiscountStrategyKind, IDiscountStrategy> _strategies;

    public PricingService(OrderDbContext db, ICatalogClient catalog, TimeProvider timeProvider, IEnumerable<IDiscountStrategy> strategies)
    {
        _db = db;
        _catalog = catalog;
        _timeProvider = timeProvider;
        _strategies = strategies.ToDictionary(s => s.Kind);
    }

    public async Task<IReadOnlyList<CouponOptionResponse>> ListCouponsAsync(int customerId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var allocations = await _db.CustomerCoupons.AsNoTracking().Include(a => a.Coupon)
            .Where(a => a.CustomerId == customerId && a.UsedQuantity < a.IssuedQuantity && a.Coupon.IsActive)
            .ToListAsync(cancellationToken);
        // SQLite (tests) cannot translate DateTimeOffset comparisons; the set is small, so filter in memory.
        return allocations
            .Where(a => a.Coupon.StartsAtUtc <= now && a.Coupon.ExpiresAtUtc > now)
            .OrderBy(a => a.Coupon.MinimumSpend).ThenByDescending(a => a.Coupon.DiscountAmount)
            .Select(a => ToCoupon(a.Coupon, a.IssuedQuantity - a.UsedQuantity))
            .ToList();
    }

    /// <summary>Prices the customer's cart with live Catalog prices (preview before checkout).</summary>
    public async Task<PricingOutcome> QuoteCartAsync(int customerId, string? couponCode, CancellationToken cancellationToken)
    {
        var cart = await _db.CartItems.AsNoTracking().Where(c => c.CustomerId == customerId).ToListAsync(cancellationToken);
        var lines = new List<PricedLine>();
        foreach (var item in cart)
        {
            var dish = await _catalog.GetDishAsync(item.DishId, cancellationToken);
            if (dish is null || !dish.IsAvailable)
                return new(null, PricingErrorCode.ItemUnavailable, $"Menu item {item.DishId} is no longer available.");
            lines.Add(new PricedLine(dish.MerchantId, dish.Id, dish.Price, item.Quantity));
        }
        return await PriceAsync(customerId, lines, couponCode, cancellationToken);
    }

    /// <summary>
    /// Prices the given lines: merchant offers per merchant (from Catalog), then one platform coupon
    /// on the discounted food total, then the delivery fee.
    /// </summary>
    public async Task<PricingOutcome> PriceAsync(
        int customerId,
        IReadOnlyList<PricedLine> lines,
        string? couponCode,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return new(null, PricingErrorCode.EmptyCart, "Add at least one item before checking out.");

        var now = _timeProvider.GetUtcNow();
        var merchants = new List<MerchantPricingResponse>();
        foreach (var group in lines.GroupBy(l => l.MerchantId).OrderBy(g => g.Key))
        {
            var merchant = await _catalog.GetMerchantAsync(group.Key, cancellationToken);
            var offers = await _catalog.GetOffersAsync(group.Key, cancellationToken);
            var subtotal = Money.Round(group.Sum(l => Money.Round(l.UnitPrice * l.Quantity)));
            var discount = Strategy(DiscountStrategyKind.MerchantOffer).Apply(new DiscountContext(subtotal, now, offers)).Amount;
            merchants.Add(new MerchantPricingResponse(group.Key, merchant?.Name ?? $"Merchant {group.Key}", subtotal, discount, Money.Round(subtotal - discount)));
        }

        var foodSubtotal = Money.Round(merchants.Sum(m => m.Subtotal));
        var merchantDiscount = Money.Round(merchants.Sum(m => m.MerchantDiscount));
        var discountedFood = Money.Round(foodSubtotal - merchantDiscount);
        CouponOptionResponse? selected = null;
        var couponDiscount = 0m;

        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            var code = couponCode.Trim().ToUpperInvariant();
            var coupon = await _db.PlatformCoupons.AsNoTracking().SingleOrDefaultAsync(c => c.Code == code, cancellationToken);
            if (coupon is null)
                return new(null, PricingErrorCode.CouponNotFound, "Coupon code was not found.");
            var allocation = await _db.CustomerCoupons.AsNoTracking()
                .SingleOrDefaultAsync(a => a.CustomerId == customerId && a.CouponId == coupon.Id, cancellationToken);
            if (allocation is null)
                return new(null, PricingErrorCode.CouponNotOwned, "This coupon has not been issued to your account.");
            if (allocation.UsedQuantity >= allocation.IssuedQuantity)
                return new(null, PricingErrorCode.CouponAlreadyUsed, "You have no remaining uses for this coupon.");

            var decision = Strategy(DiscountStrategyKind.PlatformCoupon).Apply(new DiscountContext(discountedFood, now, Coupon: coupon));
            if (!decision.IsValid)
                return new(null, decision.ErrorCode, decision.Message);
            couponDiscount = decision.Amount;
            selected = ToCoupon(coupon, allocation.IssuedQuantity - allocation.UsedQuantity);
        }

        var deliveryFee = Money.Round(DefaultDeliveryFee);
        var total = Money.Round(Math.Max(0m, discountedFood - couponDiscount) + deliveryFee);
        return new(new PricingResult(foodSubtotal, merchantDiscount, couponDiscount, deliveryFee, total, selected, merchants));
    }

    private IDiscountStrategy Strategy(DiscountStrategyKind kind) =>
        _strategies.TryGetValue(kind, out var s) ? s : throw new InvalidOperationException($"Discount strategy {kind} is not registered.");

    private static CouponOptionResponse ToCoupon(PlatformCoupon c, int remaining) =>
        new(c.Id, c.Code, c.Name, c.MinimumSpend, c.DiscountAmount, c.ExpiresAtUtc, remaining);
}
