using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by Order.API after an order is persisted. The checkout saga consumes this to start the
/// reserve-stock workflow. Carries enough context for future consumers (e.g. analytics,
/// notification) without re-querying Order.
/// </summary>
// v11: wire URN pinned to the pre-v11 MassTransit default (urn:message:&lt;Namespace&gt;:&lt;TypeName&gt;)
// so the rename to OrderSubmittedEventV1 is invisible on the bus. Existing queue topology and any
// in-flight messages keep working. Future OrderSubmittedEventV2 will declare a different URN
// (e.g. "urn:message:SimpleStore.Contracts:OrderSubmittedEventV2") so consumers can route on it.
[MessageUrn("urn:message:SimpleStore.Contracts:OrderSubmittedEvent")]
public sealed record OrderSubmittedEventV1
{
    /// <summary>
    /// Schema version of this event. Defaults to 1 so old payloads (no Version field) deserialize
    /// as Version=1 — System.Text.Json fills missing properties with the C# default initializer.
    /// </summary>
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public int OrderId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public decimal TotalAmount { get; init; }
    public string ShippingAddress { get; init; } = string.Empty;
    public IReadOnlyList<OrderSubmittedLineItem> Items { get; init; } = Array.Empty<OrderSubmittedLineItem>();
}

// Nested DTO — versions with its parent event, not on its own. Same convention as Inventory's
// StockReservedV1.LineData (see CLAUDE.md "Conventions").
public sealed record OrderSubmittedLineItem
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}
