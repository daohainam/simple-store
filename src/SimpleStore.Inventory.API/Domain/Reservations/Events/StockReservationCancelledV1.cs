namespace SimpleStore.Inventory.API.Domain.Reservations.Events;

// Domain event emitted when a stock reservation is released — the checkout saga's compensating
// action when payment fails after stock was reserved (v12). Adds the held quantities back to
// stock_levels.OnHand (stock IN).
//
// Wire type string: "simplestore.inventory.reservation.cancelled.v1".
// The trailing .v1 is the versioning anchor — additive changes keep .v1.
//
// NoteId   = the reservation id (named NoteId to satisfy IInventoryDomainEvent).
// CorrelationId = the checkout-saga correlation, recorded as provenance so the projector can stamp
//            it onto the outgoing StockReservationCancelledEventV1 (the saga keys on it).
// Lines    = the released quantities (mirrors what was reserved), so the projector restores OnHand.
// CancelledAt = audit instant (DateTimeOffset, explicit zone).
public sealed record StockReservationCancelledV1 : IInventoryDomainEvent
{
    public Guid NoteId { get; init; }
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public DateTimeOffset CancelledAt { get; init; }
    public IReadOnlyList<LineData> Lines { get; init; } = [];

    public sealed record LineData
    {
        public int ProductId { get; init; }
        public int Quantity { get; init; }
    }
}
