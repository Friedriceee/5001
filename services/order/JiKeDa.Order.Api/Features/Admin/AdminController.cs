using JiKeDa.Order.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiKeDa.Order.Api.Features.Admin;

[ApiController, Route("api/admin"), Authorize(Roles = OrderRoles.Administrator)]
public sealed class AdminController(AdminService service) : ControllerBase
{
    [HttpGet("coupons")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<AdminCouponResponse>>>> Coupons(CancellationToken ct) =>
        Ok(ApiResult<IReadOnlyList<AdminCouponResponse>>.Ok(await service.CouponsAsync(ct)));

    [HttpPost("coupons")]
    public async Task<ActionResult<ApiResult<AdminCouponResponse>>> Create(CreateCouponRequest request, CancellationToken ct)
    {
        var result = await service.CreateCouponAsync(request, ct);
        return result.Conflict ? Conflict(ApiResult<AdminCouponResponse>.Fail("Coupon code already exists."))
            : StatusCode(StatusCodes.Status201Created, ApiResult<AdminCouponResponse>.Ok(result.Coupon!));
    }

    [HttpPut("coupons/{id:int}")]
    public async Task<ActionResult<ApiResult<AdminCouponResponse>>> Update(int id, UpdateCouponRequest request, CancellationToken ct)
    {
        var coupon = await service.UpdateCouponAsync(id, request, ct);
        return coupon is null ? NotFound(ApiResult<AdminCouponResponse>.Fail("Coupon was not found.")) : Ok(ApiResult<AdminCouponResponse>.Ok(coupon));
    }

    [HttpPut("coupons/{id:int}/status")]
    public async Task<ActionResult<ApiResult<AdminCouponResponse>>> Status(int id, CouponStatusRequest request, CancellationToken ct)
    {
        var coupon = await service.SetCouponStatusAsync(id, request.IsActive, ct);
        return coupon is null ? NotFound(ApiResult<AdminCouponResponse>.Fail("Coupon was not found.")) : Ok(ApiResult<AdminCouponResponse>.Ok(coupon));
    }

    [HttpGet("coupon-allocations")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<CouponAllocationResponse>>>> Allocations(CancellationToken ct) =>
        Ok(ApiResult<IReadOnlyList<CouponAllocationResponse>>.Ok(await service.AllocationsAsync(ct)));

    [HttpPost("coupons/{id:int}/issue")]
    public async Task<ActionResult<ApiResult<CouponAllocationResponse>>> Issue(int id, IssueCouponRequest request, CancellationToken ct)
    {
        var allocation = await service.IssueAsync(id, request, ct);
        return allocation is null ? NotFound(ApiResult<CouponAllocationResponse>.Fail("Coupon was not found."))
            : StatusCode(StatusCodes.Status201Created, ApiResult<CouponAllocationResponse>.Ok(allocation));
    }

    [HttpGet("orders")]
    public async Task<ActionResult<ApiResult<PagedResult<AdminOrderResponse>>>> Orders(
        [FromQuery] int? userId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(ApiResult<PagedResult<AdminOrderResponse>>.Ok(await service.OrdersAsync(userId, status, page, pageSize, cancellationToken)));
}
