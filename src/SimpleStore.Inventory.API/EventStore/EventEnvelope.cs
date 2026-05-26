using SimpleStore.Inventory.API.Domain;

namespace SimpleStore.Inventory.API.EventStore;

// Carrier for a single event passing through the IEventStore port.
// Hides KurrentDB.Client's EventData / ResolvedEvent so the rest of the
// app does not depend on the SDK's types.
public sealed record EventEnvelope
{
    public required Guid EventId { get; init; }
    public required string Type { get; init; }
    public required string StreamName { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
    public EventStorePosition? Position { get; init; }
    public IInventoryDomainEvent? DomainEvent { get; init; }
}
