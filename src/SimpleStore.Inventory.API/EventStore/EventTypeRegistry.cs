using SimpleStore.Inventory.API.Domain;
using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;
using SimpleStore.Inventory.API.Domain.Reservations.Events;

namespace SimpleStore.Inventory.API.EventStore;

// Bidirectional map between CLR event types and KurrentDB wire-type strings.
// New event types: add a single line here. The .v1 suffix is the versioning
// anchor — additive changes keep .v1, breaking changes go to .v2.
//
// v11 — V2 introduction recipe (illustrative, no V2 exists yet):
//   1. Add a constant + entry below: e.g. `StockReservedV2Type = "simplestore.inventory.reservation.reserved.v2"`.
//   2. Add an `ApplyStockReservedV2Async(StockReservedV2 evt, bool isLive, CancellationToken ct)`
//      overload to InventoryProjector and a switch case in InventoryProjectionService.ApplyOneAsync.
//   3. Optionally implement `IEventUpcaster<StockReservedV1, StockReservedV2>` and resolve it from
//      DI in the projector to flow V1 events through the V2 apply method during a cold replay.
// Historic events keep their wire string forever — V1 wire type stays in this registry as long as
// any V1 event remains in KurrentDB (i.e. effectively forever).
public sealed class EventTypeRegistry
{
    public const string DeliveryNoteIssuedV1Type = "simplestore.inventory.delivery-note.issued.v1";
    public const string ReceiptNoteRecordedV1Type = "simplestore.inventory.receipt-note.recorded.v1";
    public const string StockReservedV1Type = "simplestore.inventory.reservation.reserved.v1";

    private readonly Dictionary<Type, string> _clrToWire;
    private readonly Dictionary<string, Type> _wireToClr;

    public EventTypeRegistry()
    {
        _clrToWire = new()
        {
            [typeof(DeliveryNoteIssuedV1)] = DeliveryNoteIssuedV1Type,
            [typeof(ReceiptNoteRecordedV1)] = ReceiptNoteRecordedV1Type,
            [typeof(StockReservedV1)] = StockReservedV1Type,
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
