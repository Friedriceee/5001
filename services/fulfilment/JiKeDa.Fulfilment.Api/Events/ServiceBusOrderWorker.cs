using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace JiKeDa.Fulfilment.Api.Events;

/// <summary>Competing consumers; acknowledge only after the projection and inbox receipt commit.</summary>
public sealed class ServiceBusOrderWorker(
    ServiceBusClient client, IServiceScopeFactory scopes, IConfiguration config,
    ILogger<ServiceBusOrderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = client.CreateProcessor(config["Messaging:QueueName"] ?? "order-confirmed",
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false, MaxConcurrentCalls = 1, PrefetchCount = 0,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
            });
        processor.ProcessMessageAsync += async args =>
        {
            var message = JsonSerializer.Deserialize<OrderEvent>(args.Message.Body.ToString(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (message is null || message.Id <= 0 || message.Type != "OrderConfirmed")
            {
                await args.DeadLetterMessageAsync(args.Message, "UnsupportedEvent", "Invalid event envelope", args.CancellationToken);
                return;
            }
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OrderEventConsumer>().ConsumeQueueAsync(message, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += args =>
        {
            logger.LogWarning(args.Exception, "Service Bus consumer failed in {Source}; unacknowledged messages can be retried", args.ErrorSource);
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
