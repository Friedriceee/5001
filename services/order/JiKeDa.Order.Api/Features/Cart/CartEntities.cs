namespace JiKeDa.Order.Api.Features.Cart;

/// <summary>A persistent cart line. Prices are never stored here; they are read from Catalog.</summary>
public class CartItem
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int MerchantId { get; set; }
    public int DishId { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Customer delivery address. Owned by Order because only orders use it.</summary>
public class DeliveryAddress
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Address { get; set; } = string.Empty;
    public string? HouseNumber { get; set; }
    public string ContactName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
}
