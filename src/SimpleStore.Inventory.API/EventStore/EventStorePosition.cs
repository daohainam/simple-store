namespace SimpleStore.Inventory.API.EventStore;

// Global cursor in KurrentDB's $all stream. KurrentDB exposes this as a
// (commit, prepare) pair of 64-bit integers. We wrap it so the rest of the
// app never sees the SDK's Position type.
public readonly record struct EventStorePosition(ulong CommitPosition, ulong PreparePosition);
