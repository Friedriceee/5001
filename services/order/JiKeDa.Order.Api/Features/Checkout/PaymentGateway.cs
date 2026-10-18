using Microsoft.Extensions.Options;

namespace JiKeDa.Order.Api.Features.Checkout;

public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    /// <summary>Allows the decline test card so the failure path can be demonstrated; a real gateway never would.</summary>
    public bool AllowSimulatedDecline { get; set; } = true;
}

public sealed record PaymentGatewayRequest(int CustomerId, decimal Amount, string Method, string IdempotencyKey);
public sealed record PaymentGatewayResult(bool Approved, string Reference, string? DeclineReason = null);

/// <summary>Boundary to the payment provider (ported from the monolith). Only a simulator exists.</summary>
public interface IPaymentGateway
{
    bool SupportsMethod(string method);
    Task<PaymentGatewayResult> ChargeAsync(PaymentGatewayRequest request, CancellationToken cancellationToken);
}

public sealed class SimulatedPaymentGateway(IOptions<PaymentOptions> options) : IPaymentGateway
{
    public const string ApproveMethod = "SimulatedCard";
    public const string DeclineMethod = "SimulatedCardDeclined";

    public bool SupportsMethod(string method) =>
        method == ApproveMethod || (method == DeclineMethod && options.Value.AllowSimulatedDecline);

    public Task<PaymentGatewayResult> ChargeAsync(PaymentGatewayRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(request.Method == DeclineMethod
            ? new PaymentGatewayResult(false, string.Empty, "The simulated card was declined.")
            : new PaymentGatewayResult(true, "SIM-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant()));
}
