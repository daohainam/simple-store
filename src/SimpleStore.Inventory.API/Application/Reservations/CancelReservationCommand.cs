namespace SimpleStore.Inventory.API.Application.Reservations;

// Application-layer command carrying the checkout saga's release-stock (compensation) intent into
// the write side. ReservationId identifies the existing reservation stream to release.
public sealed record CancelReservationCommand(
    Guid CorrelationId,
    Guid ReservationId,
    int OrderId);
