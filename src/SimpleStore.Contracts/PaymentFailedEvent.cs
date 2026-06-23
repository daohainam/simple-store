using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by SimpleStore.Payment.API when an order's payment cannot be charged — typically the
/// account balance is below the order total. The checkout saga consumes this and compensates: it
/// releases the already-reserved stock, then cancels the order.
/// </summary>
// v12 — brand-new event; URN follows the repo convention (V1 omits the suffix). See OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:PaymentFailedEvent")]
public sealed record PaymentFailedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    /// <summary>One of the <see cref="PaymentFailureReason"/> constants.</summary>
    public string Reason { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateTimeOffset FailedAt { get; init; }
}

/// <summary>
/// Well-known values for <see cref="PaymentFailedEventV1.Reason"/>. Use these constants on the
/// publish side so the string never diverges between Payment.API and the checkout saga.
/// </summary>
public static class PaymentFailureReason
{
    public const string InsufficientFunds = "InsufficientFunds";
}
