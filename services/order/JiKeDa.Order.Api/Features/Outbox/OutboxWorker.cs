using System.Text.Json;
using JiKeDa.Order.Api.Clients;
using JiKeDa.Order.Api.Data;
using JiKeDa.Order.Api.Features.Checkout;
using Microsoft.EntityFrameworkCore;

namespace JiKeDa.Order.Api.Features.Outbox;

/// <summary>
/// Background compensation: retries queued Catalog releases, and abandons checkout attempts that
/// stopped half-way (for example the process restarted after reserving stock) by releasing their
/// reservation. Safe to run on several replicas: a release is idempotent in Catalog.
/// </summary>
public sealed class OutboxWorker(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<OutboxWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StaleAttemptAge = TimeSpan.FromMinutes(5);
    public const int MaxAttempts = 50;

    public static OutboxMessage ReleaseMessage(string reservationId, DateTime now) => new()
    {
        Type = OutboxTypes.ReleaseReservation,
        Payload = JsonSerializer.Serialize(new { reservationId }),
        CreatedAtUtc = now,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Outbox pass failed; retrying in {Interval}", Interval);
            }
            await Task.Delay(Interval, timeProvider, stoppingToken);
        }
    }

    /// <summary>One pass. Public so tests can drive it without waiting for the timer.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<ICatalogClient>();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // 1. Abandon stale attempts: queue their release and delete them, in one transaction.
        var cutoff = now - StaleAttemptAge;
        var stale = await db.CheckoutAttempts
            .Where(a => a.Status == CheckoutAttemptStatus.InProgress && a.CreatedAtUtc < cutoff)
            .ToListAsync(cancellationToken);
        foreach (var attempt in stale)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deleted = await db.CheckoutAttempts
                .Where(a => a.Id == attempt.Id && a.Status == CheckoutAttemptStatus.InProgress)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted == 1)
            {
                db.Outbox.Add(ReleaseMessage(attempt.ReservationId, now));
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Abandoned stale checkout attempt {AttemptId}", attempt.Id);
            }
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        // 2. Carry out queued releases.
        var pending = await db.Outbox
            .Where(m => m.Type == OutboxTypes.ReleaseReservation && m.ProcessedAtUtc == null && m.Attempts < MaxAttempts)
            .OrderBy(m => m.Id).Take(50)
            .ToListAsync(cancellationToken);
        foreach (var message in pending)
        {
            var reservationId = JsonDocument.Parse(message.Payload).RootElement.GetProperty("reservationId").GetString()!;
            message.Attempts++;
            try
            {
                await catalog.ReleaseAsync(reservationId, cancellationToken); // false = never reserved: nothing to undo
                message.ProcessedAtUtc = now;
            }
            catch (CatalogUnavailableException error)
            {
                logger.LogWarning(error, "Release of {ReservationId} failed (attempt {Attempt})", reservationId, message.Attempts);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
