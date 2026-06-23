using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by Inventory.API's projector when a reservation has been released and the held stock
/// added back to stock_levels.OnHand (cold-start replay does not republish history). The checkout
/// saga consumes this — it confirms the compensation completed — and then cancels the order.
/// Reuses <see cref="ReservationLineItem"/> from ReserveStockRequestedEvent.cs.
/// </summary>
// v12 — brand-new event; URN follows the repo convention (V1 omits the suffix). See OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:StockReservationCancelledEvent")]
public sealed record StockReservationCancelledEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public Guid ReservationId { get; init; }
    public int OrderId { get; init; }
    public DateTimeOffset CancelledAt { get; init; }
    public IReadOnlyList<ReservationLineItem> Lines { get; init; } = Array.Empty<ReservationLineItem>();
}
