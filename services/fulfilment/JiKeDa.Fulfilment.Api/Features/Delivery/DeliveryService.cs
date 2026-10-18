using System.ComponentModel.DataAnnotations;
using System.Data;
using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Fulfilment.Api.Features.Delivery;

public sealed record AvailableDeliveryResponse(int OrderId, string Zone, int AddressId, decimal TotalAmount, DateTime OrderedAtUtc);
public sealed record DeliveryResponse(int OrderId, int RiderId, string Status, int AddressId, decimal TotalAmount,
    DateTime AssignedAtUtc, DateTime? PickedUpAtUtc, DateTime? DeliveringAtUtc, DateTime? CompletedAtUtc);
public sealed class DeliveryActionRequest { [Required, MaxLength(40)] public string Action { get; set; } = string.Empty; }
public enum DeliveryOutcome { Success, NotFound, NotReady, AlreadyTaken, RiderIneligible, InvalidAction, InvalidTransition }
public sealed record DeliveryResult(DeliveryOutcome Outcome, DeliveryResponse? Delivery, string? Message);

/// <summary>Rider dispatch and tracking. The event contains an address id only, so ZoneA is the documented local default until an address-read contract is introduced.</summary>
public sealed class DeliveryService(FulfilmentDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AvailableDeliveryResponse>> AvailableAsync(CancellationToken cancellationToken) =>
        await db.Orders.AsNoTracking().Where(o => o.Status == FulfilmentOrderStatus.ReadyForDelivery)
            .Where(o => !db.Deliveries.Any(d => d.OrderId == o.Id)).OrderBy(o => o.OccurredAtUtc).Take(100)
            .Select(o => new AvailableDeliveryResponse(o.ExternalOrderId, "ZoneA", o.AddressId, o.TotalAmount, o.OccurredAtUtc)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DeliveryResponse>> MineAsync(int riderId, CancellationToken cancellationToken)
    {
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => d.RiderId == riderId)
            .Include(d => d.Rider).Join(db.Orders.AsNoTracking(), d => d.OrderId, o => o.Id, (d, o) => new { d, o })
            .OrderByDescending(x => x.d.AssignedAtUtc).Take(100).ToListAsync(cancellationToken);
        return deliveries.Select(x => ToResponse(x.d, x.o)).ToList();
    }

    public async Task<DeliveryResult> AcceptAsync(int externalOrderId, int riderId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var order = await db.Orders.SingleOrDefaultAsync(o => o.ExternalOrderId == externalOrderId, cancellationToken);
            if (order is null) return new(DeliveryOutcome.NotFound, null, "Order was not found.");
            if (await db.Deliveries.AnyAsync(d => d.OrderId == order.Id, cancellationToken)) return new(DeliveryOutcome.AlreadyTaken, null, "Order has already been claimed.");
            if (order.Status != FulfilmentOrderStatus.ReadyForDelivery) return new(DeliveryOutcome.NotReady, null, "Order is not ready for delivery.");
            var rider = await db.Riders.SingleOrDefaultAsync(r => r.Id == riderId, cancellationToken);
            if (rider is null) return new(DeliveryOutcome.NotFound, null, "Rider profile was not found.");
            if (!rider.IsActive || !rider.IsAvailable || rider.ActiveDeliveryCount >= rider.Capacity || !string.Equals(rider.Zone, "ZoneA", StringComparison.OrdinalIgnoreCase))
                return new(DeliveryOutcome.RiderIneligible, null, "Rider is not eligible for this delivery.");
            var delivery = new RiderDelivery { OrderId = order.Id, RiderId = riderId, AssignedAtUtc = timeProvider.GetUtcNow().UtcDateTime };
            db.Deliveries.Add(delivery);
            rider.ActiveDeliveryCount++;
            order.Status = FulfilmentOrderStatus.Assigned;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(DeliveryOutcome.Success, ToResponse(delivery, order), null);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(DeliveryOutcome.AlreadyTaken, null, "Order has already been claimed.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<DeliveryResult> AdvanceAsync(int externalOrderId, int riderId, DeliveryActionRequest request, CancellationToken cancellationToken)
    {
        var action = request.Action.Trim().ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var order = await db.Orders.SingleOrDefaultAsync(o => o.ExternalOrderId == externalOrderId, cancellationToken);
            if (order is null) return new(DeliveryOutcome.NotFound, null, "Delivery was not found.");
            var row = await db.Deliveries.SingleOrDefaultAsync(d => d.RiderId == riderId && d.OrderId == order.Id, cancellationToken);
            if (row is null) return new(DeliveryOutcome.NotFound, null, "Delivery was not found.");
            var next = (row.Status, action) switch
            {
                (DeliveryStatus.Assigned, "pickup") => DeliveryStatus.PickedUp,
                (DeliveryStatus.PickedUp, "startdelivering") => DeliveryStatus.Delivering,
                (DeliveryStatus.Delivering, "complete") => DeliveryStatus.Completed,
                _ when action is not ("pickup" or "startdelivering" or "complete") => (DeliveryStatus?)null,
                _ => row.Status,
            };
            if (next is null) return new(DeliveryOutcome.InvalidAction, null, "Action must be PickUp, StartDelivering, or Complete.");
            if (next == row.Status) return new(DeliveryOutcome.InvalidTransition, null, $"Cannot apply {request.Action} while delivery is {row.Status}.");
            var now = timeProvider.GetUtcNow().UtcDateTime;
            row.Status = next.Value;
            order.Status = next.Value switch
            {
                DeliveryStatus.PickedUp => FulfilmentOrderStatus.PickedUp,
                DeliveryStatus.Delivering => FulfilmentOrderStatus.Delivering,
                _ => FulfilmentOrderStatus.Completed,
            };
            if (next == DeliveryStatus.PickedUp) row.PickedUpAtUtc = now;
            if (next == DeliveryStatus.Delivering) row.DeliveringAtUtc = now;
            if (next == DeliveryStatus.Completed)
            {
                row.CompletedAtUtc = now;
                var rider = await db.Riders.SingleAsync(r => r.Id == riderId, cancellationToken);
                rider.ActiveDeliveryCount = Math.Max(0, rider.ActiveDeliveryCount - 1);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(DeliveryOutcome.Success, ToResponse(row, order), null);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    private static DeliveryResponse ToResponse(RiderDelivery delivery, FulfilmentOrder order) => new(order.ExternalOrderId, delivery.RiderId, delivery.Status.ToString(),
        order.AddressId, order.TotalAmount, delivery.AssignedAtUtc, delivery.PickedUpAtUtc, delivery.DeliveringAtUtc, delivery.CompletedAtUtc);
}

[ApiController, Route("api/riders"), Authorize(Roles = Roles.Rider)]
public sealed class RiderController(DeliveryService service) : ControllerBase
{
    [HttpGet("available")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<AvailableDeliveryResponse>>>> Available(CancellationToken cancellationToken) =>
        Ok(ApiResult<IReadOnlyList<AvailableDeliveryResponse>>.Ok(await service.AvailableAsync(cancellationToken)));
    [HttpGet("deliveries")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<DeliveryResponse>>>> Mine(CancellationToken cancellationToken) =>
        Ok(ApiResult<IReadOnlyList<DeliveryResponse>>.Ok(await service.MineAsync(User.GetProfileId(), cancellationToken)));
}

[ApiController, Route("api/deliveries"), Authorize(Roles = Roles.Rider)]
public sealed class DeliveriesController(DeliveryService service) : ControllerBase
{
    [HttpPost("{orderId:int}/accept")]
    public async Task<ActionResult<ApiResult<DeliveryResponse>>> Accept(int orderId, CancellationToken cancellationToken) =>
        await Respond(service.AcceptAsync(orderId, User.GetProfileId(), cancellationToken));
    [HttpPut("{orderId:int}/status")]
    public async Task<ActionResult<ApiResult<DeliveryResponse>>> Advance(int orderId, DeliveryActionRequest request, CancellationToken cancellationToken) =>
        await Respond(service.AdvanceAsync(orderId, User.GetProfileId(), request, cancellationToken));

    private static async Task<ActionResult<ApiResult<DeliveryResponse>>> Respond(Task<DeliveryResult> task)
    {
        var result = await task;
        var body = ApiResult<DeliveryResponse>.Fail(result.Message!);
        return result.Outcome switch
        {
            DeliveryOutcome.Success => new OkObjectResult(ApiResult<DeliveryResponse>.Ok(result.Delivery!)),
            DeliveryOutcome.NotFound => new NotFoundObjectResult(body),
            DeliveryOutcome.NotReady or DeliveryOutcome.InvalidAction => new BadRequestObjectResult(body),
            DeliveryOutcome.RiderIneligible => new ObjectResult(body) { StatusCode = StatusCodes.Status403Forbidden },
            _ => new ConflictObjectResult(body),
        };
    }
}
