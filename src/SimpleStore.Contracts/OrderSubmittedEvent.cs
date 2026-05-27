namespace SimpleStore.Contracts;

/// <summary>
/// Published by Order.API after an order is persisted. Catalog.API consumes this to decrement
/// Product.Stock for each line item. Carries enough context for future consumers (e.g. analytics,
/// notification) without re-querying Order.
/// </summary>
public sealed record OrderSubmittedEvent
{
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public decimal TotalAmount { get; init; }
    public string ShippingAddress { get; init; } = string.Empty;
    public IReadOnlyList<OrderSubmittedLineItem> Items { get; init; } = Array.Empty<OrderSubmittedLineItem>();
}

public sealed record OrderSubmittedLineItem
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}
