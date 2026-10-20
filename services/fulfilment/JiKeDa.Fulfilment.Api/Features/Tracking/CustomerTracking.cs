using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Fulfilment.Api.Features.Tracking;

/// <summary>Read-only delivery projection shared by the customer order history.</summary>
public sealed record CustomerOrderTrackingResponse(
    int OrderId,
    string KitchenStatus,
    string OverallStatus,
    string? DeliveryStatus,
    int? RiderId,
    string? RiderName,
    string? RiderPhone,
    DateTime? AssignedAtUtc,
    DateTime? PickedUpAtUtc,
    DateTime? DeliveringAtUtc,
    DateTime? CompletedAtUtc);

public sealed class CustomerTrackingService(FulfilmentDbContext db)
{
    public async Task<IReadOnlyList<CustomerOrderTrackingResponse>> MineAsync(
        int customerId,
        CancellationToken cancellationToken)
    {
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == customerId)
            .Include(o => o.MerchantFulfilments)
            .OrderByDescending(o => o.ExternalOrderId)
            .Take(100)
            .ToListAsync(cancellationToken);
        var orderIds = orders.Select(o => o.Id).ToArray();
        var deliveries = await db.Deliveries.AsNoTracking().Include(d => d.Rider)
            .Where(d => orderIds.Contains(d.OrderId))
            .ToDictionaryAsync(d => d.OrderId, cancellationToken);

        return orders.Select(order =>
        {
            deliveries.TryGetValue(order.Id, out var delivery);
            return new CustomerOrderTrackingResponse(
                order.ExternalOrderId,
                KitchenStatus(order),
                order.Status.ToString(),
                delivery?.Status.ToString(),
                delivery?.RiderId,
                delivery?.Rider.Name,
                delivery?.Rider.PhoneNumber,
                delivery?.AssignedAtUtc,
                delivery?.PickedUpAtUtc,
                delivery?.DeliveringAtUtc,
                delivery?.CompletedAtUtc);
        }).ToList();
    }

    private static string KitchenStatus(FulfilmentOrder order)
    {
        var statuses = order.MerchantFulfilments.Select(f => f.Status).ToArray();
        if (statuses.Length == 0) return MerchantFulfilmentStatus.Confirmed.ToString();
        if (statuses.Any(s => s == MerchantFulfilmentStatus.Rejected)) return MerchantFulfilmentStatus.Rejected.ToString();
        if (statuses.All(s => s == MerchantFulfilmentStatus.ReadyForDelivery)) return MerchantFulfilmentStatus.ReadyForDelivery.ToString();
        if (statuses.Any(s => s == MerchantFulfilmentStatus.Preparing)) return MerchantFulfilmentStatus.Preparing.ToString();
        if (statuses.Any(s => s == MerchantFulfilmentStatus.Accepted)) return MerchantFulfilmentStatus.Accepted.ToString();
        return MerchantFulfilmentStatus.Confirmed.ToString();
    }
}

[ApiController, Route("api/fulfilment/tracking"), Authorize(Roles = Roles.Customer)]
public sealed class CustomerTrackingController(CustomerTrackingService service) : ControllerBase
{
    [HttpGet("orders")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<CustomerOrderTrackingResponse>>>> Mine(CancellationToken cancellationToken) =>
        Ok(ApiResult<IReadOnlyList<CustomerOrderTrackingResponse>>.Ok(
            await service.MineAsync(User.GetProfileId(), cancellationToken)));
}
