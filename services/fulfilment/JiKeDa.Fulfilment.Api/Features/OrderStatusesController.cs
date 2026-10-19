using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Fulfilment.Api.Features;

public sealed record FulfilmentOrderStatusResponse(int OrderId, string Status);

/// <summary>Internal read model used by Order to show the latest fulfilment state to a customer.</summary>
[ApiController]
[Route("internal/fulfilment/order-statuses")]
[Authorize(Roles = Roles.Service)]
public sealed class OrderStatusesController(FulfilmentDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<FulfilmentOrderStatusResponse>>>> Get(
        [FromQuery] int[] orderIds,
        CancellationToken cancellationToken)
    {
        var ids = orderIds.Where(id => id > 0).Distinct().Take(100).ToArray();
        var statuses = await db.Orders.AsNoTracking()
            .Where(order => ids.Contains(order.ExternalOrderId))
            .OrderByDescending(order => order.ExternalOrderId)
            .Select(order => new FulfilmentOrderStatusResponse(order.ExternalOrderId, order.Status.ToString()))
            .ToListAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<FulfilmentOrderStatusResponse>>.Ok(statuses));
    }
}
