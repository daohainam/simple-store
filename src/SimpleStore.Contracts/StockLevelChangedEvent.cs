namespace SimpleStore.Contracts;

/// <summary>
/// Published by Inventory.API's projector whenever stock_levels.OnHand changes (reservations,
/// receipt notes, delivery notes). Catalog.API consumes this to refresh its denormalized
/// Product.Stock cache. Unlike the saga events, this is not correlated to any specific workflow
/// — it is a broadcast cache refresh.
/// </summary>
public sealed record StockLevelChangedEvent
{
    public int ProductId { get; init; }
    public int NewOnHand { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
    public string Cause { get; init; } = string.Empty;
}
