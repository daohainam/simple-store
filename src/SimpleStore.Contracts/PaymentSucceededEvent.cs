using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by SimpleStore.Payment.API when an order's payment is charged successfully (the account
/// had sufficient balance and was debited). The checkout saga consumes this to confirm the order.
/// </summary>
// v12 — brand-new event; URN follows the repo convention (V1 omits the suffix). See OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:PaymentSucceededEvent")]
public sealed record PaymentSucceededEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public Guid TransactionId { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset PaidAt { get; init; }
}
