namespace JiKeDa.Order.Api.Features.Checkout;

public enum CheckoutAttemptStatus { InProgress = 0, Completed = 1 }

/// <summary>
/// One checkout per (CustomerId, IdempotencyKey), enforced by a unique index. Inserting this row
/// is the idempotency lock: only the request that inserted it reserves stock, and only that
/// request may release it. A failed attempt is deleted, so the customer can retry with the same key.
/// </summary>
public sealed class CheckoutAttempt
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of the normalised request; detects a key reused with a different payload.</summary>
    public string RequestHash { get; set; } = string.Empty;

    public CheckoutAttemptStatus Status { get; set; }
    public int? OrderId { get; set; }

    /// <summary>The response returned to the first request, so a replay returns the same body.</summary>
    public string? ResponseJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string ReservationId => $"chk-{Id}";
}

/// <summary>A successful simulated payment. Declined attempts leave nothing behind.</summary>
public sealed class Payment
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
