namespace SimpleStore.Inventory.API.Domain.Reservations.Events;

// Domain event emitted when stock is reserved for an order (stock OUT, soft hold).
//
// Wire type string: "simplestore.inventory.reservation.reserved.v1".
// The trailing .v1 is the versioning anchor — additive changes keep .v1.
//
// NoteId   = the reservation id (named NoteId to satisfy IInventoryDomainEvent; the projector
//            keys streams and read rows off it just like delivery/receipt notes).
// CorrelationId = the checkout-saga correlation that triggered this reservation, recorded as
//            provenance so the async projector can stamp it onto the outgoing StockReservedEvent
//            (the saga keys on it). For non-saga callers (e.g. a future admin reservation) this
//            may be Guid.Empty.
// OrderId  = soft reference to Order.API's order (int).
// ReservedAt = audit instant (DateTimeOffset, explicit zone).
//
// v9 will add StockReservationCommittedV1 / StockReservationCancelledV1 to this aggregate.
public sealed record StockReservedV1 : IInventoryDomainEvent
{
    public Guid NoteId { get; init; }
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public DateTimeOffset ReservedAt { get; init; }
    public IReadOnlyList<LineData> Lines { get; init; } = [];

    public sealed record LineData
    {
        public int ProductId { get; init; }
        public int Quantity { get; init; }
    }
}
