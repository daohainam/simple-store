using MassTransit;

namespace SimpleStore.Contracts;

/// <summary>
/// Published by Inventory.API's CreateReservationHandler directly (NOT via the projector) when
/// a reservation request is rejected. Rejected commands emit no domain event by design, so the
/// handler writes this integration event through its own outbox flush and never touches KurrentDB.
/// </summary>
// v11: wire URN pinned to the pre-v11 default — see OrderSubmittedEvent.cs.
[MessageUrn("urn:message:SimpleStore.Contracts:StockReservationFailedEvent")]
public sealed record StockReservationFailedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    public Guid ReservationId { get; init; }
    public int OrderId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<ShortageLine> ShortageLines { get; init; } = Array.Empty<ShortageLine>();
    public DateTimeOffset FailedAt { get; init; }
}

public sealed record ShortageLine
{
    public int ProductId { get; init; }
    public int Requested { get; init; }
    public int Available { get; init; }
}
