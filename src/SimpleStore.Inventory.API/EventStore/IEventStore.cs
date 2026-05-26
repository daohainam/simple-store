using SimpleStore.Inventory.API.Domain;

namespace SimpleStore.Inventory.API.EventStore;

// Technology-agnostic event-store port. The only KurrentDB-specific file
// is KurrentEventStore.cs; everything else in the service depends on this
// interface. Swap the adapter, swap the store.
public interface IEventStore
{
    // Append domain events to a stream. The adapter is responsible for
    // serialization (CLR event -> wire bytes + wire-type string).
    // Throws ConcurrencyConflictException if the optimistic-concurrency
    // condition is not met (e.g. NoStream but the stream already exists).
    Task AppendAsync(
        string streamName,
        IReadOnlyList<IInventoryDomainEvent> events,
        AppendCondition condition,
        CancellationToken ct);

    // Live + catch-up subscription to $all, filtered to events whose stream
    // name starts with one of the given prefixes. The sequence resumes from
    // the supplied position (null = from the very start of the log).
    IAsyncEnumerable<EventEnvelope> SubscribeAllAsync(
        string[] streamNamePrefixes,
        EventStorePosition? fromPosition,
        CancellationToken ct);

    // Read all events of a single stream in order. Used by repositories
    // (none in v7) to rehydrate aggregates.
    IAsyncEnumerable<EventEnvelope> ReadStreamAsync(
        string streamName,
        CancellationToken ct);
}

// Thrown by AppendAsync when the expected-state condition is violated.
// The endpoint layer maps this to HTTP 409 Conflict.
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string streamName, Exception? inner = null)
        : base($"Concurrency conflict on stream '{streamName}'.", inner)
    {
        StreamName = streamName;
    }

    public string StreamName { get; }
}
