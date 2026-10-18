namespace JiKeDa.Order.Api.Features.Outbox;

public static class OutboxTypes
{
    /// <summary>Published for Fulfilment; read through GET /internal/order/events.</summary>
    public const string OrderConfirmed = "OrderConfirmed";

    /// <summary>A Catalog release that failed in-request; retried by <see cref="OutboxWorker"/>.</summary>
    public const string ReleaseReservation = "ReleaseReservation";
}

/// <summary>
/// Written in the same transaction as the state change it describes, so an event or a pending
/// compensation is never lost when a process stops between the database commit and the HTTP call.
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when a ReleaseReservation has been carried out. Events stay readable in the feed.</summary>
    public DateTime? ProcessedAtUtc { get; set; }

    public int Attempts { get; set; }
}
