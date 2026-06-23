using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by Inventory.API's projector whenever stock_levels.OnHand changes (reservations,
/// receipt notes, delivery notes). Catalog.API consumes this to refresh its denormalized
/// Product.Stock cache. Unlike the saga events, this is not correlated to any specific workflow
/// — it is a broadcast cache refresh.
/// </summary>
// v11: wire URN pinned to the pre-v11 default — see OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:StockLevelChangedEvent")]
public sealed record StockLevelChangedEventV1
{
    public int Version { get; init; } = 1;
    public int ProductId { get; init; }
    public int NewOnHand { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
    /// <summary>One of the <see cref="StockChangeCause"/> constants.</summary>
    public string Cause { get; init; } = string.Empty;
}

/// <summary>
/// Well-known values for <see cref="StockLevelChangedEventV1.Cause"/>. Use these constants on
/// the publish side so the string never diverges between producer and consumer.
/// </summary>
public static class StockChangeCause
{
    public const string DeliveryNote = "DeliveryNote";
    public const string ReceiptNote = "ReceiptNote";
    public const string ReservationCreated = "ReservationCreated";
    // v12: a reservation was released (compensation), adding the held quantity back to OnHand.
    public const string ReservationCancelled = "ReservationCancelled";
}
