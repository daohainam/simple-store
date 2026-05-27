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

    // True once the $all subscription has caught up to the live tail. The projector uses this to
    // suppress integration-event publishing during a cold-start replay (FromAll.Start), so wiping
    // the read DB and replaying does NOT re-publish the entire history to RabbitMQ.
    public bool IsLive { get; init; }
}
