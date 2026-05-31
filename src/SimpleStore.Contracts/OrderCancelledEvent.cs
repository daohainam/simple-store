using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga when it transitions to the Cancelled state (either because
/// Inventory rejected the reservation, or because the 30 s timeout expired before any response).
/// Order.API's OrderCancelledConsumer flips the Order.Status to "Cancelled".
/// </summary>
// v11: wire URN pinned to the pre-v11 default — see OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:OrderCancelledEvent")]
public sealed record OrderCancelledEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CancelledAt { get; init; }
}
