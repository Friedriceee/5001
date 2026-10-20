using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JiKeDa.Order.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Outbox;

public sealed class QueueObservation
{
    public long Active { get; set; }
    public long DeadLetters { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
}

/// <summary>Transactional outbox publisher. A crash after send can redeliver; the consumer inbox deduplicates.</summary>
public sealed class ServiceBusPublisher(
    IServiceScopeFactory scopes, ServiceBusSender sender, ServiceBusAdministrationClient administration,
    QueueObservation observation, IConfiguration config, ILogger<ServiceBusPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = config["Messaging:QueueName"] ?? "order-confirmed";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                // Row locks prevent two Order replicas publishing the same batch at the same time.
                var pending = await db.Outbox.FromSqlRaw("""
                    SELECT * FROM "Outbox"
                    WHERE "Type" = 'OrderConfirmed' AND "ProcessedAtUtc" IS NULL
                    ORDER BY "Id" LIMIT 20 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(stoppingToken);
                foreach (var row in pending)
                {
                    using var payload = JsonDocument.Parse(row.Payload);
                    var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(new
                    {
                        id = row.Id, type = row.Type, payload = payload.RootElement,
                    })) { MessageId = $"order-outbox-{row.Id}", ContentType = "application/json", Subject = row.Type };
                    if (payload.RootElement.TryGetProperty("correlationId", out var correlation))
                        message.CorrelationId = correlation.GetString();
                    if (payload.RootElement.TryGetProperty("traceParent", out var trace) && trace.ValueKind == JsonValueKind.String)
                        message.ApplicationProperties["Diagnostic-Id"] = trace.GetString()!;
                    await sender.SendMessageAsync(message, stoppingToken);
                    row.ProcessedAtUtc = DateTime.UtcNow;
                    row.Attempts++;
                }
                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                var state = (await administration.GetQueueRuntimePropertiesAsync(queue, stoppingToken)).Value;
                observation.Active = state.ActiveMessageCount;
                observation.DeadLetters = state.DeadLetterMessageCount;
                observation.ObservedAtUtc = DateTime.UtcNow;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Service Bus outbox publishing or queue observation failed; retrying");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
