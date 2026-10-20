namespace JiKeDa.Fulfilment.Api.Features;

public enum FulfilmentOrderStatus { AwaitingMerchant, Preparing, ReadyForDelivery, Assigned, PickedUp, Delivering, Completed, Rejected }
public enum MerchantFulfilmentStatus { Confirmed, Accepted, Preparing, ReadyForDelivery, Rejected }
public enum DeliveryStatus { Assigned, PickedUp, Delivering, Completed }

public sealed class FulfilmentOrder
{
    public int Id { get; set; }
    public int ExternalOrderId { get; set; }
    public int CustomerId { get; set; }
    public int AddressId { get; set; }
    public string? ContactName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? HouseNumber { get; set; }
    public int NeedUtensils { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal TotalAmount { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public FulfilmentOrderStatus Status { get; set; } = FulfilmentOrderStatus.AwaitingMerchant;
    public ICollection<FulfilmentLine> Lines { get; set; } = new List<FulfilmentLine>();
    public ICollection<MerchantOrderFulfilment> MerchantFulfilments { get; set; } = new List<MerchantOrderFulfilment>();
}

public sealed class FulfilmentLine
{
    public int OrderId { get; set; }
    public int DishId { get; set; }
    public int MerchantId { get; set; }
    public string DishName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public FulfilmentOrder Order { get; set; } = null!;
}

public sealed class MerchantOrderFulfilment
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int MerchantId { get; set; }
    public MerchantFulfilmentStatus Status { get; set; } = MerchantFulfilmentStatus.Confirmed;
    public string? RejectionReason { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public FulfilmentOrder Order { get; set; } = null!;
}

public sealed class Rider
{
    /// <summary>Identity's Rider profile_id; this service never reads Identity's tables.</summary>
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string Zone { get; set; } = "ZoneA";
    public bool IsActive { get; set; } = true;
    public bool IsAvailable { get; set; } = true;
    public int Capacity { get; set; } = 3;
    public int ActiveDeliveryCount { get; set; }
    public ICollection<RiderDelivery> Deliveries { get; set; } = new List<RiderDelivery>();
}

public sealed class RiderDelivery
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int RiderId { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Assigned;
    public DateTime AssignedAtUtc { get; set; }
    public DateTime? PickedUpAtUtc { get; set; }
    public DateTime? DeliveringAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Rider Rider { get; set; } = null!;
}

public sealed class EventCheckpoint
{
    public int Id { get; set; }
    public string Consumer { get; set; } = string.Empty;
    public long LastEventId { get; set; }
}
