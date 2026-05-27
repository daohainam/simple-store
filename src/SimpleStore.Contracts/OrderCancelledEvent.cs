namespace SimpleStore.Contracts;

/// <summary>
/// Published by the checkout saga when it transitions to the Cancelled state (either because
/// Inventory rejected the reservation, or because the 30 s timeout expired before any response).
/// Order.API's OrderCancelledConsumer flips the Order.Status to "Cancelled".
/// </summary>
public sealed record OrderCancelledEvent
{
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CancelledAt { get; init; }
}
