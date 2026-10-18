namespace JiKeDa.Catalog.Api.Features.Inventory;

/// <summary>
/// One line of a stock reservation, keyed by (ReservationId, DishId). The ReservationId is
/// chosen by the caller (the Order service), so a retried call never deducts twice.
/// Name and price are snapshotted so the caller receives the amount it reserved.
/// </summary>
public sealed class InventoryReservation
{
    public string ReservationId { get; set; } = string.Empty;
    public int DishId { get; set; }
    public int MerchantId { get; set; }
    public string DishName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public bool IsReleased { get; set; }
    public DateTimeOffset ReservedAtUtc { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
}
