using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga (SimpleStore.Checkout.API) to ask Inventory.API to reserve
/// stock for an order. ReservationId is saga-generated; Inventory uses it as the aggregate id
/// and as an idempotency key (AppendCondition.NoStream on stream reservation-{ReservationId}).
/// </summary>
// v11: wire URN pinned to the pre-v11 default — see OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:ReserveStockRequestedEvent")]
public sealed record ReserveStockRequestedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public Guid ReservationId { get; init; }
    public int OrderId { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public IReadOnlyList<ReservationLineItem> Lines { get; init; } = Array.Empty<ReservationLineItem>();
}

public sealed record ReservationLineItem
{
    public int ProductId { get; init; }
    public int Quantity { get; init; }
}
