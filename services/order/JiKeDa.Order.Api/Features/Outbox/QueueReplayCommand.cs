using System.Text.Json;
using Azure.Messaging.ServiceBus;
using JiKeDa.Order.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Outbox;

/// <summary>Operator-only container command for a controlled, explicitly labelled replay test.</summary>
public static class QueueReplayCommand
{
    public static async Task RunAsync(WebApplication app, int count)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var row = await db.Outbox.Where(e => e.Type == OutboxTypes.OrderConfirmed).OrderByDescending(e => e.Id).FirstAsync();
        using var payload = JsonDocument.Parse(row.Payload);
        var envelope = BinaryData.FromObjectAsJson(new { id = row.Id, type = row.Type, payload = payload.RootElement });
        var sender = app.Services.GetRequiredService<ServiceBusSender>();
        var run = Guid.NewGuid().ToString("N");
        ServiceBusMessageBatch batch = await sender.CreateMessageBatchAsync();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var message = new ServiceBusMessage(envelope)
                {
                    MessageId = $"controlled-replay-{run}-{i}", Subject = row.Type, ContentType = "application/json",
                };
                message.ApplicationProperties["controlled-replay"] = true;
                if (!batch.TryAddMessage(message))
                {
                    await sender.SendMessagesAsync(batch);
                    batch.Dispose();
                    batch = await sender.CreateMessageBatchAsync();
                    if (!batch.TryAddMessage(message)) throw new InvalidOperationException("Event exceeds the broker batch size");
                }
            }
            if (batch.Count > 0) await sender.SendMessagesAsync(batch);
        }
        finally { batch.Dispose(); }
        Console.WriteLine($"Controlled replay: {count} real queue messages, existing outbox event {row.Id}; expect no additional business orders.");
    }
}
