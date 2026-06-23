using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga (SimpleStore.Checkout.API) after stock has been reserved, asking
/// SimpleStore.Payment.API to charge the customer's account for the order total. Payment.API debits
/// the account and replies with <see cref="PaymentSucceededEventV1"/> or
/// <see cref="PaymentFailedEventV1"/>. Amount is the order total carried from OrderSubmittedEventV1.
/// </summary>
// v12 — brand-new event; URN follows the repo convention (V1 omits the suffix, a future V2 declares
// its own URN). See docs/versioning.md and OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:ProcessPaymentRequestedEvent")]
public sealed record ProcessPaymentRequestedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
}
