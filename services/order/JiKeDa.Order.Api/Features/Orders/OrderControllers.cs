using System.Text.Json;
using JiKeDa.Order.Api.Common;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Checkout;
using JiKeDa.Order.Api.Features.Outbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Orders;

[ApiController]
[Route("api/orders")]
[Authorize(Roles = OrderRoles.Customer)]
public sealed class OrdersController : ControllerBase
{
    private readonly OrderDbContext _db;
    private readonly IFulfilmentStatusClient _fulfilment;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(OrderDbContext db, IFulfilmentStatusClient fulfilment, ILogger<OrdersController> logger)
    {
        _db = db;
        _fulfilment = fulfilment;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<OrderResponse>>>> Mine(CancellationToken cancellationToken)
    {
        var customerId = User.GetProfileId();
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.CustomerId == customerId).OrderByDescending(o => o.Id).Take(100)
            .ToListAsync(cancellationToken);
        var statuses = await ReadStatusesAsync(orders.Select(order => order.Id).ToArray(), cancellationToken);
        return Ok(ApiResult<IReadOnlyList<OrderResponse>>.Ok(orders
            .Select(order => OrderResponse.From(order, statuses.GetValueOrDefault(order.Id))).ToList()));
    }

    [HttpGet("{orderId:int}")]
    public async Task<ActionResult<ApiResult<OrderResponse>>> Get(int orderId, CancellationToken cancellationToken)
    {
        var customerId = User.GetProfileId();
        var order = await _db.Orders.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId, cancellationToken);
        return order is null
            ? NotFound(ApiResult<OrderResponse>.Fail("Order was not found."))
            : Ok(ApiResult<OrderResponse>.Ok(OrderResponse.From(
                order,
                (await ReadStatusesAsync([order.Id], cancellationToken)).GetValueOrDefault(order.Id))));
    }

    private async Task<IReadOnlyDictionary<int, string>> ReadStatusesAsync(
        IReadOnlyCollection<int> orderIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _fulfilment.GetStatusesAsync(orderIds, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // Order history remains available during a temporary Fulfilment outage. The persisted
            // payment status is the safe fallback and the next refresh will show the newer state.
            _logger.LogWarning(error, "Fulfilment status is unavailable; returning persisted order status.");
            return new Dictionary<int, string>();
        }
    }
}

[ApiController]
[Route("api/checkout")]
[Authorize(Roles = OrderRoles.Customer)]
public sealed class CheckoutController : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";
    private readonly CheckoutService _checkout;

    public CheckoutController(CheckoutService checkout) => _checkout = checkout;

    /// <summary>Safe to retry with the same Idempotency-Key: a repeat returns 200 with replayed: true.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResult<CheckoutResponse>>> Checkout(
        [FromBody] CheckoutRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var correlationId = Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? HttpContext.TraceIdentifier;
        var result = await _checkout.CheckoutAsync(User.GetProfileId(), idempotencyKey, request, correlationId, cancellationToken);
        if (result.Succeeded)
        {
            var body = ApiResult<CheckoutResponse>.Ok(result.Response!);
            return result.Outcome == CheckoutOutcome.Replayed ? Ok(body) : StatusCode(StatusCodes.Status201Created, body);
        }

        var failure = ApiResult<CheckoutResponse>.Fail(result.Message!, result.Code);
        return result.Outcome switch
        {
            CheckoutOutcome.InvalidIdempotencyKey or CheckoutOutcome.UnsupportedPaymentMethod or CheckoutOutcome.PricingFailed => BadRequest(failure),
            CheckoutOutcome.AddressNotFound => NotFound(failure),
            CheckoutOutcome.PaymentDeclined => StatusCode(StatusCodes.Status402PaymentRequired, failure),
            CheckoutOutcome.ServiceUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, failure),
            _ => Conflict(failure),
        };
    }
}

public sealed record OrderEventResponse(long Id, string Type, JsonElement Payload);

/// <summary>
/// Event feed for the Fulfilment service until a broker exists: poll with the last id you processed.
/// Not routed by the gateway; any valid JWT (Fulfilment signs a service token) may read it.
/// </summary>
[ApiController]
[Route("internal/order/events")]
[Authorize]
public sealed class OrderEventsController : ControllerBase
{
    private readonly OrderDbContext _db;

    public OrderEventsController(OrderDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<ApiResult<IReadOnlyList<OrderEventResponse>>>> After(
        [FromQuery] long after = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var messages = await _db.Outbox.AsNoTracking()
            .Where(m => m.Type == OutboxTypes.OrderConfirmed && m.Id > after)
            .OrderBy(m => m.Id).Take(limit)
            .ToListAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<OrderEventResponse>>.Ok(messages
            .Select(m => new OrderEventResponse(m.Id, m.Type, JsonDocument.Parse(m.Payload).RootElement.Clone()))
            .ToList()));
    }
}
