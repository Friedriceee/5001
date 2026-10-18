using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Orders;
using JiKeDa.Order.Api.Features.Outbox;
using JiKeDa.Order.Api.Features.Pricing;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Checkout;

public sealed class CheckoutRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "addressId must be greater than 0.")]
    public int AddressId { get; set; }

    [StringLength(40, ErrorMessage = "couponCode must be at most 40 characters.")]
    public string? CouponCode { get; set; }

    [Range(0, 1, ErrorMessage = "needUtensils must be 0 or 1.")]
    public int NeedUtensils { get; set; }

    [Required(ErrorMessage = "paymentMethod is required.")]
    [StringLength(40, ErrorMessage = "paymentMethod must be at most 40 characters.")]
    public string? PaymentMethod { get; set; } = SimulatedPaymentGateway.ApproveMethod;
}

public sealed record CheckoutResponse(
    int OrderId,
    string Status,
    decimal AmountPaid,
    string PaymentReference,
    string PaymentMethod,
    DateTime PaidAtUtc,
    bool Replayed,
    PricingResult Pricing,
    IReadOnlyList<OrderLineResponse> Lines);

public enum CheckoutOutcome
{
    Success, Replayed, InvalidIdempotencyKey, IdempotencyKeyReused, CheckoutInProgress, UnsupportedPaymentMethod,
    AddressNotFound, PricingFailed, CartChanged, InsufficientStock, ItemUnavailable, PaymentDeclined, ServiceUnavailable,
}

public sealed record CheckoutResult(CheckoutOutcome Outcome, CheckoutResponse? Response = null, string? Message = null, string? Code = null)
{
    public bool Succeeded => Outcome is CheckoutOutcome.Success or CheckoutOutcome.Replayed;
}

/// <summary>
/// Turns the customer's cart into a paid order across two services.
///
/// The monolith did this in one database transaction. Stock now lives in Catalog, so checkout is a
/// short saga: (1) claim the Idempotency-Key with a CheckoutAttempt row; (2) reserve stock in Catalog
/// under a reservation id derived from that row; (3) price from the reservation's price snapshot;
/// (4) in one local transaction claim the cart, use the coupon, charge, create the order and write
/// the OrderConfirmed event to the outbox. If (3) or (4) fails, the reservation is released; if
/// Catalog cannot be reached, the release is written to the outbox and retried by the worker.
/// </summary>
public sealed class CheckoutService
{
    private static readonly Regex KeyPattern = new("^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OrderDbContext _db;
    private readonly ICatalogClient _catalog;
    private readonly PricingService _pricing;
    private readonly IPaymentGateway _gateway;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckoutService> _logger;

    public CheckoutService(OrderDbContext db, ICatalogClient catalog, PricingService pricing, IPaymentGateway gateway,
        TimeProvider timeProvider, ILogger<CheckoutService> logger)
    {
        _db = db;
        _catalog = catalog;
        _pricing = pricing;
        _gateway = gateway;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CheckoutResult> CheckoutAsync(
        int customerId,
        string? idempotencyKey,
        CheckoutRequest request,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey is null || !KeyPattern.IsMatch(idempotencyKey))
            return Fail(CheckoutOutcome.InvalidIdempotencyKey, "Idempotency-Key must be 8-64 characters using letters, digits, '-' or '_'.");
        var method = request.PaymentMethod?.Trim() ?? string.Empty;
        if (!_gateway.SupportsMethod(method))
            return Fail(CheckoutOutcome.UnsupportedPaymentMethod, "This payment method is not supported.");

        var couponCode = string.IsNullOrWhiteSpace(request.CouponCode) ? null : request.CouponCode.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.AddressId}|{couponCode}|{request.NeedUtensils}|{method}")));

        // (1) Claim the key. Only the request that inserts the attempt goes on.
        var (attempt, earlier) = await ClaimAttemptAsync(customerId, idempotencyKey, hash, cancellationToken);
        if (earlier is not null)
            return earlier;

        var reserved = false;
        try
        {
            var result = await RunAsync(attempt!, request, couponCode, method, correlationId, () => reserved = true, cancellationToken);
            if (!result.Succeeded)
                await CompensateAsync(attempt!, reserved);
            return result;
        }
        catch (CatalogUnavailableException error)
        {
            _logger.LogWarning(error, "Catalog unavailable during checkout attempt {AttemptId}", attempt!.Id);
            // The reserve call may have reached Catalog before failing, so release defensively.
            await CompensateAsync(attempt, reservedOrUnknown: true);
            return Fail(CheckoutOutcome.ServiceUnavailable, "The menu service is temporarily unavailable. Please try again.");
        }
        catch
        {
            await CompensateAsync(attempt!, reserved);
            throw;
        }
    }

    private async Task<CheckoutResult> RunAsync(
        CheckoutAttempt attempt,
        CheckoutRequest request,
        string? couponCode,
        string method,
        string? correlationId,
        Action markReserved,
        CancellationToken cancellationToken)
    {
        var customerId = attempt.CustomerId;
        var deliveryAddress = await _db.Addresses.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == request.AddressId && a.CustomerId == customerId, cancellationToken);
        if (deliveryAddress is null)
            return Fail(CheckoutOutcome.AddressNotFound, "Delivery address was not found.");

        var cart = await _db.CartItems.AsNoTracking().Where(c => c.CustomerId == customerId).OrderBy(c => c.Id).ToListAsync(cancellationToken);
        if (cart.Count == 0)
            return Fail(CheckoutOutcome.PricingFailed, "Add at least one item before checking out.", nameof(PricingErrorCode.EmptyCart));

        // (2) Reserve stock in Catalog.
        var reserve = await _catalog.ReserveAsync(attempt.ReservationId,
            cart.Select(c => new ReservationLine(c.DishId, c.Quantity)).ToList(), cancellationToken);
        if (reserve.Status != ReserveStatus.Reserved)
        {
            return reserve.Status == ReserveStatus.InsufficientStock
                ? Fail(CheckoutOutcome.InsufficientStock, reserve.Message ?? "There is not enough stock for one of the items.")
                : Fail(CheckoutOutcome.ItemUnavailable, reserve.Message ?? "One of the items is no longer available.");
        }
        markReserved();
        var reservation = reserve.Reservation!;

        // (3) Price from the reservation's snapshot, so the amount charged matches the stock held.
        var quote = await _pricing.PriceAsync(customerId,
            reservation.Items.Select(i => new PricedLine(i.MerchantId, i.DishId, i.UnitPrice, i.Quantity)).ToList(),
            couponCode, cancellationToken);
        if (!quote.Succeeded)
            return Fail(CheckoutOutcome.PricingFailed, quote.Message!, quote.ErrorCode!.Value.ToString());
        var pricing = quote.Pricing!;

        // (4) One local transaction for everything Order owns.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var cartIds = cart.Select(c => c.Id).ToArray();
        var claimed = await _db.CartItems.Where(c => c.CustomerId == customerId && cartIds.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);
        if (claimed != cartIds.Length)
            return await RollbackAsync(transaction, Fail(CheckoutOutcome.CartChanged, "Your cart changed while checking out. Review it and try again."));

        if (pricing.SelectedCoupon is { } coupon)
        {
            var used = await _db.CustomerCoupons
                .Where(a => a.CustomerId == customerId && a.CouponId == coupon.Id && a.UsedQuantity < a.IssuedQuantity)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.UsedQuantity, a => a.UsedQuantity + 1), cancellationToken);
            if (used != 1)
                return await RollbackAsync(transaction, Fail(CheckoutOutcome.PricingFailed,
                    "You have no remaining uses for this coupon.", nameof(PricingErrorCode.CouponAlreadyUsed)));
        }

        var charge = await _gateway.ChargeAsync(new PaymentGatewayRequest(customerId, pricing.Total, method, attempt.IdempotencyKey), cancellationToken);
        if (!charge.Approved)
            return await RollbackAsync(transaction, Fail(CheckoutOutcome.PaymentDeclined, charge.DeclineReason ?? "The payment was declined."));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var order = new CustomerOrder
        {
            CustomerId = customerId,
            AddressId = request.AddressId,
            NeedUtensils = request.NeedUtensils,
            Status = OrderStatus.Paid,
            Subtotal = pricing.Subtotal,
            MerchantDiscount = pricing.MerchantDiscount,
            CouponDiscount = pricing.CouponDiscount,
            DeliveryFee = pricing.DeliveryFee,
            Total = pricing.Total,
            CouponId = pricing.SelectedCoupon?.Id,
            ReservationId = reservation.ReservationId,
            CreatedAtUtc = now,
            Lines = reservation.Items.Select(i => new OrderLine
            {
                DishId = i.DishId, MerchantId = i.MerchantId, DishName = i.DishName, UnitPrice = i.UnitPrice, Quantity = i.Quantity,
            }).ToList(),
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);

        _db.Payments.Add(new Payment { OrderId = order.Id, Amount = pricing.Total, Method = method, Reference = charge.Reference, CreatedAtUtc = now });
        _db.Outbox.Add(new OutboxMessage
        {
            Type = OutboxTypes.OrderConfirmed,
            CreatedAtUtc = now,
            Payload = JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid().ToString("N"),
                eventType = OutboxTypes.OrderConfirmed,
                eventVersion = 1,
                orderId = order.Id,
                customerId,
                addressId = order.AddressId,
                deliveryAddress = new
                {
                    deliveryAddress.ContactName,
                    deliveryAddress.PhoneNumber,
                    deliveryAddress.Address,
                    deliveryAddress.HouseNumber,
                },
                needUtensils = order.NeedUtensils,
                items = order.Lines.Select(l => new { merchantId = l.MerchantId, dishId = l.DishId, dishName = l.DishName, quantity = l.Quantity, unitPrice = l.UnitPrice }),
                deliveryFee = order.DeliveryFee,
                totalAmount = order.Total,
                occurredAt = now,
                correlationId,
            }, Json),
        });

        var response = new CheckoutResponse(order.Id, order.Status.ToString(), order.Total, charge.Reference, method,
            DateTime.SpecifyKind(now, DateTimeKind.Utc), false, pricing, OrderResponse.From(order).Lines);
        _db.Attach(attempt);
        attempt.Status = CheckoutAttemptStatus.Completed;
        attempt.OrderId = order.Id;
        attempt.ResponseJson = JsonSerializer.Serialize(response, Json);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CheckoutResult(CheckoutOutcome.Success, response);
    }

    /// <summary>Inserts the attempt, or explains why an earlier attempt with the same key wins.</summary>
    private async Task<(CheckoutAttempt? Attempt, CheckoutResult? Earlier)> ClaimAttemptAsync(
        int customerId, string key, string hash, CancellationToken cancellationToken)
    {
        var existing = await FindAttemptAsync(customerId, key, hash, cancellationToken);
        if (existing is not null)
            return (null, existing);

        var attempt = new CheckoutAttempt
        {
            CustomerId = customerId, IdempotencyKey = key, RequestHash = hash,
            Status = CheckoutAttemptStatus.InProgress, CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
        };
        _db.CheckoutAttempts.Add(attempt);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same key inserted first.
            _db.ChangeTracker.Clear();
            return (null, await FindAttemptAsync(customerId, key, hash, cancellationToken)
                ?? Fail(CheckoutOutcome.CheckoutInProgress, "A checkout with this Idempotency-Key is in progress."));
        }
        _db.Entry(attempt).State = EntityState.Detached;
        return (attempt, null);
    }

    private async Task<CheckoutResult?> FindAttemptAsync(int customerId, string key, string hash, CancellationToken cancellationToken)
    {
        var attempt = await _db.CheckoutAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.CustomerId == customerId && a.IdempotencyKey == key, cancellationToken);
        if (attempt is null) return null;
        if (attempt.RequestHash != hash)
            return Fail(CheckoutOutcome.IdempotencyKeyReused, "This Idempotency-Key was already used for a different checkout request.");
        if (attempt.Status != CheckoutAttemptStatus.Completed)
            return Fail(CheckoutOutcome.CheckoutInProgress, "A checkout with this Idempotency-Key is in progress.");
        var stored = JsonSerializer.Deserialize<CheckoutResponse>(attempt.ResponseJson!, Json)!;
        return new CheckoutResult(CheckoutOutcome.Replayed, stored with { Replayed = true });
    }

    /// <summary>
    /// Undo a failed attempt: release the stock (or queue the release) and delete the attempt so the
    /// customer can retry with the same key. Runs on a fresh change tracker with no request token,
    /// so it completes even when the client has gone away.
    /// </summary>
    private async Task CompensateAsync(CheckoutAttempt attempt, bool reservedOrUnknown)
    {
        _db.ChangeTracker.Clear();
        var queueRelease = false;
        if (reservedOrUnknown)
        {
            try
            {
                await _catalog.ReleaseAsync(attempt.ReservationId, CancellationToken.None);
            }
            catch (CatalogUnavailableException error)
            {
                _logger.LogWarning(error, "Queueing release of {ReservationId}", attempt.ReservationId);
                queueRelease = true;
            }
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(CancellationToken.None);
        await _db.CheckoutAttempts.Where(a => a.Id == attempt.Id && a.Status == CheckoutAttemptStatus.InProgress)
            .ExecuteDeleteAsync(CancellationToken.None);
        if (queueRelease)
        {
            _db.Outbox.Add(OutboxWorker.ReleaseMessage(attempt.ReservationId, _timeProvider.GetUtcNow().UtcDateTime));
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task<CheckoutResult> RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CheckoutResult result)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();
        return result;
    }

    private static CheckoutResult Fail(CheckoutOutcome outcome, string message, string? code = null) =>
        new(outcome, null, message, code ?? outcome.ToString());
}
