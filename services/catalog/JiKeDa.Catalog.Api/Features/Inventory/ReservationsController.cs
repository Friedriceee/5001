using JiKeDa.Catalog.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiKeDa.Catalog.Api.Features.Inventory;

/// <summary>
/// Service-to-service stock reservation for the Order service. The gateway has no route to
/// /internal/*, so customers cannot reserve stock directly; callers still need a valid JWT
/// (Order forwards the customer's token).
/// </summary>
[ApiController]
[Route("internal/catalog/reservations")]
[Authorize]
public sealed class ReservationsController : ControllerBase
{
    private readonly InventoryService _inventory;

    public ReservationsController(InventoryService inventory) => _inventory = inventory;

    [HttpPost]
    public async Task<ActionResult<ApiResult<ReservationResponse>>> Reserve(
        [FromBody] ReserveInventoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _inventory.ReserveAsync(request.ReservationId!, request.Items!, cancellationToken);
        return result.Outcome switch
        {
            ReserveOutcome.Reserved => StatusCode(StatusCodes.Status201Created, ApiResult<ReservationResponse>.Ok(result.Reservation!)),
            ReserveOutcome.Replayed => Ok(ApiResult<ReservationResponse>.Ok(result.Reservation!)),
            ReserveOutcome.DishNotFound => NotFound(ApiResult<ReservationResponse>.Fail(result.Message!, "DISH_NOT_FOUND")),
            ReserveOutcome.Conflict => Conflict(ApiResult<ReservationResponse>.Fail(result.Message!, "RESERVATION_CONFLICT")),
            ReserveOutcome.InsufficientStock => BadRequest(ApiResult<ReservationResponse>.Fail(result.Message!, "INSUFFICIENT_STOCK")),
            ReserveOutcome.Unavailable => BadRequest(ApiResult<ReservationResponse>.Fail(result.Message!, "UNAVAILABLE")),
            _ => BadRequest(ApiResult<ReservationResponse>.Fail(result.Message!, "INVALID_REQUEST")),
        };
    }

    [HttpPost("{reservationId}/release")]
    public async Task<ActionResult<ApiResult<ReleaseResponse>>> Release(
        string reservationId,
        [FromQuery] int? merchantId,
        CancellationToken cancellationToken)
    {
        var result = await _inventory.ReleaseAsync(reservationId, merchantId, cancellationToken);
        return result.Outcome == ReleaseOutcome.NotFound
            ? NotFound(ApiResult<ReleaseResponse>.Fail(result.Message!, "RESERVATION_NOT_FOUND"))
            : Ok(ApiResult<ReleaseResponse>.Ok(result.Release!));
    }
}
