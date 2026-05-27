namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga when it transitions to the Confirmed state. Order.API's
/// OrderConfirmedConsumer flips the Order.Status to "Confirmed" on the row matching CorrelationId.
/// </summary>
public sealed record OrderConfirmedEvent
{
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public Guid ReservationId { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; }
}
