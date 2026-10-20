using System.ComponentModel.DataAnnotations;
using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Fulfilment.Api.Features.Merchant;

public sealed record MerchantOrderResponse(
    int OrderId,
    int MerchantId,
    int CustomerId,
    DateTime OrderTimestamp,
    string Status,
    string OverallStatus,
    string? RejectionReason,
    decimal MerchantSubtotal,
    int ItemCount,
    string? RiderName,
    string? RiderPhone,
    string? DeliveryStatus,
    IReadOnlyList<MerchantOrderLineResponse> Items,
    int AddressId,
    int NeedUtensils,
    decimal TotalAmount,
    DateTime OrderedAtUtc);
public sealed record MerchantOrderLineResponse(int DishId, string DishName, int Quantity, decimal UnitPrice, decimal LineTotal);
public sealed class MerchantActionRequest
{
    [Required, MaxLength(40)] public string Action { get; set; } = string.Empty;
    [MaxLength(250)] public string? Reason { get; set; }
}
public enum MerchantActionOutcome { Success, NotFound, InvalidAction, InvalidTransition }
public sealed record MerchantActionResult(MerchantActionOutcome Outcome, MerchantOrderResponse? Order, string? Message);

public sealed class MerchantFulfilmentService(FulfilmentDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<MerchantOrderResponse>> MineAsync(int merchantId, CancellationToken cancellationToken)
    {
        var work = await db.MerchantFulfilments.AsNoTracking().Where(f => f.MerchantId == merchantId)
            .Include(f => f.Order).ThenInclude(o => o.Lines).OrderByDescending(f => f.Order.OccurredAtUtc).Take(100).ToListAsync(cancellationToken);
        var orderIds = work.Select(f => f.OrderId).ToArray();
        var deliveries = await db.Deliveries.AsNoTracking().Include(d => d.Rider)
            .Where(d => orderIds.Contains(d.OrderId)).ToDictionaryAsync(d => d.OrderId, cancellationToken);
        return work.Select(f => ToResponse(f, deliveries.GetValueOrDefault(f.OrderId))).ToList();
    }

    public async Task<MerchantActionResult> AdvanceAsync(int externalOrderId, int merchantId, MerchantActionRequest request, CancellationToken cancellationToken)
    {
        var action = request.Action.Trim().ToLowerInvariant();
        var next = action switch
        {
            "accept" => MerchantFulfilmentStatus.Accepted,
            "startpreparing" => MerchantFulfilmentStatus.Preparing,
            "markready" => MerchantFulfilmentStatus.ReadyForDelivery,
            "reject" => MerchantFulfilmentStatus.Rejected,
            _ => (MerchantFulfilmentStatus?)null,
        };
        if (next is null) return new(MerchantActionOutcome.InvalidAction, null, "Action must be Accept, StartPreparing, MarkReady, or Reject.");
        var fulfilment = await db.MerchantFulfilments.Include(f => f.Order).ThenInclude(o => o.Lines)
            .SingleOrDefaultAsync(f => f.MerchantId == merchantId && f.Order.ExternalOrderId == externalOrderId, cancellationToken);
        if (fulfilment is null) return new(MerchantActionOutcome.NotFound, null, "Order was not found.");
        if (!IsAllowed(fulfilment.Status, next.Value, request.Reason))
            return new(MerchantActionOutcome.InvalidTransition, null, $"Cannot apply {request.Action} while order is {fulfilment.Status}.");

        fulfilment.Status = next.Value;
        fulfilment.RejectionReason = next == MerchantFulfilmentStatus.Rejected ? request.Reason?.Trim() : null;
        fulfilment.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var all = await db.MerchantFulfilments.Where(f => f.OrderId == fulfilment.OrderId).ToListAsync(cancellationToken);
        fulfilment.Order.Status = all.Any(f => f.Status == MerchantFulfilmentStatus.Rejected)
            ? FulfilmentOrderStatus.Rejected
            : all.All(f => f.Status == MerchantFulfilmentStatus.ReadyForDelivery)
                ? FulfilmentOrderStatus.ReadyForDelivery
                : all.Any(f => f.Status is MerchantFulfilmentStatus.Preparing or MerchantFulfilmentStatus.Accepted)
                    ? FulfilmentOrderStatus.Preparing : FulfilmentOrderStatus.AwaitingMerchant;
        await db.SaveChangesAsync(cancellationToken);
        var delivery = await db.Deliveries.AsNoTracking().Include(d => d.Rider)
            .SingleOrDefaultAsync(d => d.OrderId == fulfilment.OrderId, cancellationToken);
        return new(MerchantActionOutcome.Success, ToResponse(fulfilment, delivery), null);
    }

    private static bool IsAllowed(MerchantFulfilmentStatus current, MerchantFulfilmentStatus next, string? reason) =>
        (current, next) switch
        {
            (MerchantFulfilmentStatus.Confirmed, MerchantFulfilmentStatus.Accepted) => true,
            (MerchantFulfilmentStatus.Confirmed or MerchantFulfilmentStatus.Accepted, MerchantFulfilmentStatus.Rejected) => !string.IsNullOrWhiteSpace(reason),
            (MerchantFulfilmentStatus.Accepted, MerchantFulfilmentStatus.Preparing) => true,
            (MerchantFulfilmentStatus.Preparing, MerchantFulfilmentStatus.ReadyForDelivery) => true,
            _ => false,
        };

    private static MerchantOrderResponse ToResponse(MerchantOrderFulfilment work, RiderDelivery? delivery)
    {
        var items = work.Order.Lines.Where(l => l.MerchantId == work.MerchantId).OrderBy(l => l.DishId)
            .Select(l => new MerchantOrderLineResponse(l.DishId, l.DishName, l.Quantity, l.UnitPrice, l.UnitPrice * l.Quantity)).ToList();
        return new(
            work.Order.ExternalOrderId,
            work.MerchantId,
            work.Order.CustomerId,
            work.Order.OccurredAtUtc,
            work.Status.ToString(),
            work.Order.Status.ToString(),
            work.RejectionReason,
            items.Sum(i => i.LineTotal),
            items.Sum(i => i.Quantity),
            delivery?.Rider.Name,
            delivery?.Rider.PhoneNumber,
            delivery?.Status.ToString(),
            items,
            work.Order.AddressId,
            work.Order.NeedUtensils,
            work.Order.TotalAmount,
            work.Order.OccurredAtUtc);
    }
}

[ApiController, Route("api/fulfilment/orders"), Authorize(Roles = Roles.Merchant)]
public sealed class MerchantFulfilmentController(MerchantFulfilmentService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<MerchantOrderResponse>>>> Mine(CancellationToken cancellationToken) =>
        Ok(ApiResult<IReadOnlyList<MerchantOrderResponse>>.Ok(await service.MineAsync(User.GetProfileId(), cancellationToken)));

    [HttpPut("{orderId:int}/status")]
    public async Task<ActionResult<ApiResult<MerchantOrderResponse>>> Advance(int orderId, MerchantActionRequest request, CancellationToken cancellationToken)
    {
        var result = await service.AdvanceAsync(orderId, User.GetProfileId(), request, cancellationToken);
        var body = ApiResult<MerchantOrderResponse>.Fail(result.Message!);
        return result.Outcome switch
        {
            MerchantActionOutcome.Success => Ok(ApiResult<MerchantOrderResponse>.Ok(result.Order!)),
            MerchantActionOutcome.NotFound => NotFound(body),
            MerchantActionOutcome.InvalidAction => BadRequest(body),
            _ => Conflict(body),
        };
    }
}
