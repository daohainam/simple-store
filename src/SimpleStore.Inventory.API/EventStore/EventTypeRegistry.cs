using SimpleStore.Inventory.API.Domain;
using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;

namespace SimpleStore.Inventory.API.EventStore;

// Bidirectional map between CLR event types and KurrentDB wire-type strings.
// New event types: add a single line here. The .v1 suffix is the versioning
// anchor — additive changes keep .v1, breaking changes go to .v2.
public sealed class EventTypeRegistry
{
    public const string DeliveryNoteIssuedV1Type = "simplestore.inventory.delivery-note.issued.v1";
    public const string ReceiptNoteRecordedV1Type = "simplestore.inventory.receipt-note.recorded.v1";

    private readonly Dictionary<Type, string> _clrToWire;
    private readonly Dictionary<string, Type> _wireToClr;

    public EventTypeRegistry()
    {
        _clrToWire = new()
        {
            [typeof(DeliveryNoteIssuedV1)] = DeliveryNoteIssuedV1Type,
            [typeof(ReceiptNoteRecordedV1)] = ReceiptNoteRecordedV1Type,
        };
        _wireToClr = _clrToWire.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    public string WireTypeFor(IInventoryDomainEvent @event)
    {
        if (_clrToWire.TryGetValue(@event.GetType(), out var type))
            return type;
        throw new InvalidOperationException(
            $"No wire type registered for CLR event {@event.GetType().FullName}.");
    }

    public Type? ClrTypeFor(string wireType) =>
        _wireToClr.TryGetValue(wireType, out var t) ? t : null;
}
