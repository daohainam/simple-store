namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga (SimpleStore.Checkout.API) to ask Inventory.API to reserve
/// stock for an order. ReservationId is saga-generated; Inventory uses it as the aggregate id
/// and as an idempotency key (AppendCondition.NoStream on stream reservation-{ReservationId}).
/// </summary>
public sealed record ReserveStockRequestedEvent
{
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
