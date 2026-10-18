namespace JiKeDa.Order.Api.Features.Pricing;

public sealed class PlatformCoupon
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal MinimumSpend { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>A coupon allocation owned by one customer, redeemed atomically during checkout.</summary>
public sealed class CustomerCoupon
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int CouponId { get; set; }
    public int IssuedQuantity { get; set; }
    public int UsedQuantity { get; set; }

    public PlatformCoupon Coupon { get; set; } = null!;
}
