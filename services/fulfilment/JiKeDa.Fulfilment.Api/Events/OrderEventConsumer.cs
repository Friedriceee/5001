using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using JiKeDa.Fulfilment.Api.Common;
using JiKeDa.Fulfilment.Api.Data;
using JiKeDa.Fulfilment.Api.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JiKeDa.Fulfilment.Api.Events;

public sealed record OrderEvent(long Id, string Type, JsonElement Payload);
public sealed record OrderConfirmedAddress(string ContactName, string PhoneNumber, string Address, string? HouseNumber);
public sealed record OrderConfirmedEvent(int OrderId, int CustomerId, int AddressId, OrderConfirmedAddress? DeliveryAddress, int NeedUtensils,
    IReadOnlyList<OrderConfirmedLine> Items, decimal DeliveryFee, decimal TotalAmount, DateTime OccurredAt, string? CorrelationId);
public sealed record OrderConfirmedLine(int MerchantId, int DishId, string DishName, int Quantity, decimal UnitPrice);

public interface IOrderEventClient
{
    Task<IReadOnlyList<OrderEvent>> GetAfterAsync(long after, CancellationToken cancellationToken);
}

/// <summary>Signs the short-lived service JWT required by Order's internal event feed.</summary>
public sealed class ServiceTokenProvider(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public string Create()
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "fulfilment-service"), new Claim("role", "Service")],
            notBefore: now, expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class OrderEventClient(HttpClient http, ServiceTokenProvider tokens) : IOrderEventClient
{
    private sealed record Envelope<T>(bool Success, T? Data, string? Message, string? Code);

    public async Task<IReadOnlyList<OrderEvent>> GetAfterAsync(long after, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"internal/order/events?after={after}&limit=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Create());
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("Order event feed is unavailable.", error); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Order event feed returned HTTP {(int)response.StatusCode}.");
            return (await response.Content.ReadFromJsonAsync<Envelope<List<OrderEvent>>>(cancellationToken))?.Data ?? [];
        }
    }
}

/// <summary>Idempotently projects OrderConfirmed events and records the feed position in the same transaction.</summary>
public sealed class OrderEventConsumer(FulfilmentDbContext db, IOrderEventClient events, TimeProvider timeProvider)
{
    public const string ConsumerName = "order-confirmed-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await db.EventCheckpoints.SingleOrDefaultAsync(c => c.Consumer == ConsumerName, cancellationToken);
        var after = checkpoint?.LastEventId ?? 0;
        var batch = await events.GetAfterAsync(after, cancellationToken);
        foreach (var message in batch.OrderBy(e => e.Id))
            await ConsumeAsync(message, cancellationToken);
        return batch.Count;
    }

    private async Task ConsumeAsync(OrderEvent message, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var checkpoint = await db.EventCheckpoints.SingleOrDefaultAsync(c => c.Consumer == ConsumerName, cancellationToken);
        if ((checkpoint?.LastEventId ?? 0) >= message.Id) return;
        if (message.Type == "OrderConfirmed")
        {
            var order = JsonSerializer.Deserialize<OrderConfirmedEvent>(message.Payload.GetRawText(), Json)
                ?? throw new InvalidOperationException("OrderConfirmed payload is invalid.");
            if (!await db.Orders.AnyAsync(o => o.ExternalOrderId == order.OrderId, cancellationToken))
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                db.Orders.Add(new FulfilmentOrder
                {
                    ExternalOrderId = order.OrderId, CustomerId = order.CustomerId, AddressId = order.AddressId,
                    ContactName = order.DeliveryAddress?.ContactName,
                    PhoneNumber = order.DeliveryAddress?.PhoneNumber,
                    DeliveryAddress = order.DeliveryAddress?.Address,
                    HouseNumber = order.DeliveryAddress?.HouseNumber,
                    NeedUtensils = order.NeedUtensils, DeliveryFee = order.DeliveryFee, TotalAmount = order.TotalAmount,
                    CorrelationId = order.CorrelationId, OccurredAtUtc = DateTime.SpecifyKind(order.OccurredAt, DateTimeKind.Utc),
                    Lines = order.Items.Select(i => new FulfilmentLine { MerchantId = i.MerchantId, DishId = i.DishId, DishName = i.DishName, Quantity = i.Quantity, UnitPrice = i.UnitPrice }).ToList(),
                    MerchantFulfilments = order.Items.Select(i => i.MerchantId).Distinct().Select(merchantId =>
                        new MerchantOrderFulfilment { MerchantId = merchantId, Status = MerchantFulfilmentStatus.Confirmed, UpdatedAtUtc = now }).ToList(),
                });
            }
        }
        if (checkpoint is null) db.EventCheckpoints.Add(new EventCheckpoint { Consumer = ConsumerName, LastEventId = message.Id });
        else checkpoint.LastEventId = message.Id;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class OrderEventPollingWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<OrderEventPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("EventConsumer:PollSeconds", 2), 1, 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OrderEventConsumer>().RunOnceAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning(error, "Order event poll failed; retrying."); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
