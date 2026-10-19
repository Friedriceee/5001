using System.ComponentModel.DataAnnotations;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Admin;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
public sealed record AdminCouponResponse(int Id, string Code, string Name, decimal MinimumSpend,
    decimal DiscountAmount, DateTimeOffset StartsAtUtc, DateTimeOffset ExpiresAtUtc, bool IsActive,
    int IssuedQuantity, int UsedQuantity);
public sealed record CouponAllocationResponse(int Id, int UserId, string CustomerName, int CouponId,
    string CouponCode, int IssuedQuantity, int UsedQuantity, int RemainingQuantity);
public sealed record AdminOrderResponse(int OrderId, int UserId, string Status, decimal Price,
    DateTime OrderTimestamp, int ItemCount);

public sealed class CreateCouponRequest
{
    [Required, StringLength(30)] public string Code { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Range(0, 1000000)] public decimal MinimumSpend { get; set; }
    [Range(typeof(decimal), "0.01", "1000000")] public decimal DiscountAmount { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class UpdateCouponRequest
{
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Range(0, 1000000)] public decimal MinimumSpend { get; set; }
    [Range(typeof(decimal), "0.01", "1000000")] public decimal DiscountAmount { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class CouponStatusRequest { public bool IsActive { get; set; } }
public sealed class IssueCouponRequest
{
    [Range(1, int.MaxValue)] public int UserId { get; set; }
    [Range(1, 1000)] public int Quantity { get; set; }
}

public sealed class AdminService(OrderDbContext db, IFulfilmentStatusClient fulfilment)
{
    public async Task<IReadOnlyList<AdminCouponResponse>> CouponsAsync(CancellationToken cancellationToken)
    {
        var coupons = await db.PlatformCoupons.AsNoTracking().OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var totals = await db.CustomerCoupons.AsNoTracking().GroupBy(item => item.CouponId)
            .Select(group => new { CouponId = group.Key, Issued = group.Sum(item => item.IssuedQuantity), Used = group.Sum(item => item.UsedQuantity) })
            .ToDictionaryAsync(item => item.CouponId, cancellationToken);
        return coupons.Select(coupon => ToCoupon(coupon,
            totals.GetValueOrDefault(coupon.Id)?.Issued ?? 0,
            totals.GetValueOrDefault(coupon.Id)?.Used ?? 0)).ToList();
    }

    public async Task<(bool Conflict, AdminCouponResponse? Coupon)> CreateCouponAsync(CreateCouponRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.PlatformCoupons.AnyAsync(item => item.Code == code, cancellationToken)) return (true, null);
        ValidateDates(request.StartsAtUtc, request.ExpiresAtUtc);
        var coupon = new PlatformCoupon { Code = code, Name = request.Name.Trim(), MinimumSpend = request.MinimumSpend,
            DiscountAmount = request.DiscountAmount, StartsAtUtc = request.StartsAtUtc, ExpiresAtUtc = request.ExpiresAtUtc, IsActive = request.IsActive };
        db.PlatformCoupons.Add(coupon);
        await db.SaveChangesAsync(cancellationToken);
        return (false, ToCoupon(coupon, 0, 0));
    }

    public async Task<AdminCouponResponse?> UpdateCouponAsync(int id, UpdateCouponRequest request, CancellationToken cancellationToken)
    {
        ValidateDates(request.StartsAtUtc, request.ExpiresAtUtc);
        var coupon = await db.PlatformCoupons.FindAsync([id], cancellationToken);
        if (coupon is null) return null;
        coupon.Name = request.Name.Trim(); coupon.MinimumSpend = request.MinimumSpend; coupon.DiscountAmount = request.DiscountAmount;
        coupon.StartsAtUtc = request.StartsAtUtc; coupon.ExpiresAtUtc = request.ExpiresAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        return (await CouponsAsync(cancellationToken)).Single(item => item.Id == id);
    }

    public async Task<AdminCouponResponse?> SetCouponStatusAsync(int id, bool isActive, CancellationToken cancellationToken)
    {
        var coupon = await db.PlatformCoupons.FindAsync([id], cancellationToken);
        if (coupon is null) return null;
        coupon.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return (await CouponsAsync(cancellationToken)).Single(item => item.Id == id);
    }

    public async Task<IReadOnlyList<CouponAllocationResponse>> AllocationsAsync(CancellationToken cancellationToken) =>
        await db.CustomerCoupons.AsNoTracking().Include(item => item.Coupon)
            .OrderBy(item => item.CustomerId).ThenBy(item => item.Coupon.Code)
            .Select(item => new CouponAllocationResponse(item.Id, item.CustomerId, $"Customer #{item.CustomerId}", item.CouponId,
                item.Coupon.Code, item.IssuedQuantity, item.UsedQuantity, item.IssuedQuantity - item.UsedQuantity))
            .ToListAsync(cancellationToken);

    public async Task<CouponAllocationResponse?> IssueAsync(int couponId, IssueCouponRequest request, CancellationToken cancellationToken)
    {
        var coupon = await db.PlatformCoupons.FindAsync([couponId], cancellationToken);
        if (coupon is null) return null;
        var allocation = await db.CustomerCoupons.SingleOrDefaultAsync(
            item => item.CustomerId == request.UserId && item.CouponId == couponId, cancellationToken);
        if (allocation is null)
        {
            allocation = new CustomerCoupon { CustomerId = request.UserId, CouponId = couponId, IssuedQuantity = request.Quantity };
            db.CustomerCoupons.Add(allocation);
        }
        else allocation.IssuedQuantity += request.Quantity;
        await db.SaveChangesAsync(cancellationToken);
        return new(allocation.Id, allocation.CustomerId, $"Customer #{allocation.CustomerId}", coupon.Id, coupon.Code,
            allocation.IssuedQuantity, allocation.UsedQuantity, allocation.IssuedQuantity - allocation.UsedQuantity);
    }

    public async Task<PagedResult<AdminOrderResponse>> OrdersAsync(int? userId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Orders.AsNoTracking().Include(item => item.Lines).AsQueryable();
        if (userId.HasValue) query = query.Where(item => item.CustomerId == userId.Value);
        var source = await query.OrderByDescending(item => item.CreatedAtUtc).ToListAsync(cancellationToken);
        IReadOnlyDictionary<int, string> statuses;
        try { statuses = await fulfilment.GetStatusesAsync(source.Select(item => item.Id).ToArray(), cancellationToken); }
        catch (HttpRequestException) { statuses = new Dictionary<int, string>(); }
        var projected = source.Select(order => new AdminOrderResponse(order.Id, order.CustomerId,
            statuses.GetValueOrDefault(order.Id) ?? order.Status.ToString(), order.Total,
            DateTime.SpecifyKind(order.CreatedAtUtc, DateTimeKind.Utc), order.Lines.Sum(line => line.Quantity))).ToList();
        if (!string.IsNullOrWhiteSpace(status)) projected = projected.Where(item =>
            item.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return new(projected.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, projected.Count);
    }

    private static AdminCouponResponse ToCoupon(PlatformCoupon coupon, int issued, int used) => new(coupon.Id, coupon.Code,
        coupon.Name, coupon.MinimumSpend, coupon.DiscountAmount, coupon.StartsAtUtc, coupon.ExpiresAtUtc, coupon.IsActive, issued, used);

    private static void ValidateDates(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) throw new ArgumentException("Coupon expiry must be later than its start time.");
    }
}
