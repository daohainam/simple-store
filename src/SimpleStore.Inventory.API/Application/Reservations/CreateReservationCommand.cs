namespace SimpleStore.Inventory.API.Application.Reservations;

// Application-layer command carrying a saga's reserve-stock intent into the write side.
// ReservationId is supplied by the saga so retries collapse onto the same event-store stream.
public sealed record CreateReservationCommand(
    Guid CorrelationId,
    Guid ReservationId,
    int OrderId,
    IReadOnlyList<ReservationCommandLine> Lines);

public sealed record ReservationCommandLine(int ProductId, int Quantity);
