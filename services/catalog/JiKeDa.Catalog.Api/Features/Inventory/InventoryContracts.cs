using System.ComponentModel.DataAnnotations;

namespace JiKeDa.Catalog.Api.Features.Inventory;

public sealed class ReserveInventoryRequest
{
    [Required(ErrorMessage = "Reservation id is required.")]
    [RegularExpression("^[A-Za-z0-9._:-]{1,64}$",
        ErrorMessage = "Reservation id must be 1–64 characters: letters, digits, '.', '_', ':' or '-'.")]
    public string? ReservationId { get; set; }

    [Required(ErrorMessage = "At least one item is required.")]
    [MinLength(1, ErrorMessage = "At least one item is required.")]
    public List<ReserveInventoryItem>? Items { get; set; }
}

public sealed class ReserveInventoryItem
{
    [Range(1, int.MaxValue, ErrorMessage = "Dish id must be positive.")]
    public int DishId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be positive.")]
    public int Quantity { get; set; }
}

public sealed record ReservedItemResponse(
    int DishId,
    int MerchantId,
    string DishName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record ReservationResponse(
    string ReservationId,
    IReadOnlyList<ReservedItemResponse> Items,
    decimal TotalAmount,
    bool Replayed);

public sealed record ReleaseResponse(string ReservationId, int ReleasedLines, bool AlreadyReleased);
