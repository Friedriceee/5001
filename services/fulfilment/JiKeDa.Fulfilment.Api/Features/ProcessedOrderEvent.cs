namespace JiKeDa.Fulfilment.Api.Features;

// Unlike a high-watermark feed cursor, an inbox receipt accepts out-of-order queue delivery.
public sealed class ProcessedOrderEvent
{
    public long Id { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}
