namespace SimpleStore.Inventory.API.EventStore;

// Optimistic-concurrency condition supplied to IEventStore.AppendAsync.
// v7 only uses NoStream (aggregate creation). v8 may add StreamRevision
// for amend-style operations on existing streams.
public abstract record AppendCondition
{
    public static AppendCondition NoStream { get; } = new NoStreamCondition();

    public sealed record NoStreamCondition : AppendCondition;

    public sealed record StreamRevision(ulong ExpectedRevision) : AppendCondition;
}
