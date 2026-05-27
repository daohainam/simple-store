namespace SimpleStore.Checkout.API.Timeouts;

// Self-addressed message the saga schedules on entering AwaitingStock. If it fires before
// StockReserved / StockReservationFailed arrive, the saga cancels the order. Scheduled via the
// Quartz message scheduler backed by a persistent ADO store in checkoutdb (see Program.cs) — the
// trigger lives in Postgres, so a scheduled timeout SURVIVES a Checkout.API restart (v8b).
public sealed record ReservationTimeoutExpired
{
    public Guid CorrelationId { get; init; }
}
