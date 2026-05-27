namespace SimpleStore.Checkout.API.Timeouts;

// Self-addressed message the saga schedules on entering AwaitingStock. If it fires before
// StockReserved / StockReservationFailed arrive, the saga cancels the order. Scheduled via the
// in-memory message scheduler (see Program.cs) — note these do NOT survive a service restart.
public sealed record ReservationTimeoutExpired
{
    public Guid CorrelationId { get; init; }
}
