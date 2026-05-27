namespace SimpleStore.Contracts;

/// <summary>
/// Published by Inventory.API's projector when a StockReservedV1 domain event is applied to
/// the read tables and the subscription has caught up to the live tail (cold-start replay
/// does not republish history). The checkout saga consumes this to confirm the order.
/// </summary>
public sealed record StockReservedEvent
{
    public Guid CorrelationId { get; init; }
    public Guid ReservationId { get; init; }
    public int OrderId { get; init; }
    public DateTimeOffset ReservedAt { get; init; }
    public IReadOnlyList<ReservationLineItem> Lines { get; init; } = Array.Empty<ReservationLineItem>();
}
