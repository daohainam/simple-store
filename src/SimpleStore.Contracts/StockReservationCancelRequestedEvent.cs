using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga to ask Inventory.API to RELEASE a previously-successful stock
/// reservation — the compensating action when payment fails (or times out) after stock was reserved.
/// Inventory.API appends a cancel domain event to the reservation stream; its projector then adds the
/// held quantities back to stock_levels.OnHand and replies with <see cref="StockReservationCancelledEventV1"/>.
/// ReservationId is the same saga-generated id used by <see cref="ReserveStockRequestedEventV1"/>.
/// </summary>
// v12 — brand-new event; URN follows the repo convention (V1 omits the suffix). See OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:StockReservationCancelRequestedEvent")]
public sealed record StockReservationCancelRequestedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public Guid ReservationId { get; init; }
    public int OrderId { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
}
