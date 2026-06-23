namespace SimpleStore.Checkout.API.Timeouts;

// Self-addressed message the saga schedules on entering AwaitingPayment (v12). If it fires before
// PaymentSucceeded / PaymentFailed arrive (Payment.API unreachable), the saga compensates: it
// releases the reserved stock and cancels the order. Scheduled via the same Quartz message
// scheduler (persistent ADO store in checkoutdb) as ReservationTimeoutExpired, so it survives a
// Checkout.API restart.
public sealed record PaymentTimeoutExpired
{
    public Guid CorrelationId { get; init; }
}
