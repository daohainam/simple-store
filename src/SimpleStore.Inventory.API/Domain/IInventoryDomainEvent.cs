namespace SimpleStore.Inventory.API.Domain;

// Marker interface for the inventory bounded context's domain events.
// These are persisted to KurrentDB by the application layer and replayed
// by the projection layer. They are NOT integration events — those would
// live in SimpleStore.Contracts and be much smaller in shape.
public interface IInventoryDomainEvent
{
    Guid NoteId { get; }
}
