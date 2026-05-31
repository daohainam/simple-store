using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga when it transitions to the Confirmed state. Order.API's
/// OrderConfirmedConsumer flips the Order.Status to "Confirmed" on the row matching CorrelationId.
/// </summary>
// v11: wire URN pinned to the pre-v11 default — see OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:OrderConfirmedEvent")]
public sealed record OrderConfirmedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public Guid ReservationId { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; }
}
