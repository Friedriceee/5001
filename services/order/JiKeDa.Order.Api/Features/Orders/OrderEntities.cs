namespace JiKeDa.Order.Api.Features.Orders;

public enum OrderStatus { Paid = 1, Cancelled = 11 }

/// <summary>A paid customer order. Fulfilment status lives in the Fulfilment service.</summary>
public class CustomerOrder
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int AddressId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Paid;
    public int NeedUtensils { get; set; }
    public decimal Subtotal { get; set; }
    public decimal MerchantDiscount { get; set; }
    public decimal CouponDiscount { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Total { get; set; }
    public int? CouponId { get; set; }

    /// <summary>The Catalog reservation that holds this order's stock.</summary>
    public string ReservationId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
}

/// <summary>Order line with the name and price captured from the Catalog reservation.</summary>
public class OrderLine
{
    public int OrderId { get; set; }
    public int DishId { get; set; }
    public int MerchantId { get; set; }
    public string DishName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }

    public CustomerOrder Order { get; set; } = null!;
}

public sealed record OrderLineResponse(int MerchantId, int DishId, string DishName, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderResponse(
    int Id,
    string Status,
    int AddressId,
    int NeedUtensils,
    decimal Subtotal,
    decimal MerchantDiscount,
    decimal CouponDiscount,
    decimal DeliveryFee,
    decimal Total,
    DateTime CreatedAtUtc,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(CustomerOrder order, string? fulfilmentStatus = null) => new(
        order.Id,
        fulfilmentStatus ?? order.Status.ToString(),
        order.AddressId,
        order.NeedUtensils,
        order.Subtotal,
        order.MerchantDiscount,
        order.CouponDiscount,
        order.DeliveryFee,
        order.Total,
        DateTime.SpecifyKind(order.CreatedAtUtc, DateTimeKind.Utc),
        order.Lines.OrderBy(l => l.MerchantId).ThenBy(l => l.DishId)
            .Select(l => new OrderLineResponse(l.MerchantId, l.DishId, l.DishName, l.UnitPrice, l.Quantity, l.UnitPrice * l.Quantity))
            .ToList());
}
