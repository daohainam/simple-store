using SimpleStore.Inventory.API.Domain;

namespace SimpleStore.Inventory.API.EventStore;

// v11: scaffold for upcasting older event versions into the current shape.
//
// Not wired into the deserializer yet — there are no V2 events in v11. Documented here so the
// shape of the next breaking domain-event change is decided once, not improvised per-event.
//
// The intended flow when StockReservedV2 ships (illustrative):
//
//   1. Add the new record under Domain/Reservations/Events/StockReservedV2.cs, with whatever
//      shape the new requirement needs. KEEP the existing StockReservedV1 record — it must still
//      be deserializable for every historic event in the stream.
//
//   2. Register both in EventTypeRegistry:
//      - V1 → "simplestore.inventory.reservation.reserved.v1" (unchanged; existing events keep
//        their wire string forever)
//      - V2 → "simplestore.inventory.reservation.reserved.v2"
//
//   3. Implement `IEventUpcaster&lt;StockReservedV1, StockReservedV2&gt;` and register it in DI:
//
//        builder.Services.AddSingleton&lt;IEventUpcaster&lt;StockReservedV1, StockReservedV2&gt;, StockReservedV1ToV2&gt;();
//
//   4. Resolve the upcaster from the projector (InventoryProjectionService) when a V1 envelope
//      arrives, and dispatch the upcast V2 result into ApplyStockReservedAsync(StockReservedV2, ...).
//      The V1 Apply method becomes redundant once every consumer is on V2.
//
//   5. Old replicas keep working: they see the V2 events on the wire as unknown wire strings,
//      log a warning, and checkpoint past them. The new replica reprojects from FromAll.Start
//      after a wipe, using the upcaster to turn historic V1 events into V2 shape on the fly.
//
// The interface is intentionally tiny — there's no enrichment context, no async, no DI injection
// into the upcast call site. An upcaster is supposed to be a pure transform; if it needs to read
// state, the change isn't a pure upcast and probably wants a one-shot migration projection instead.
public interface IEventUpcaster<TOld, TNew>
    where TOld : IInventoryDomainEvent
    where TNew : IInventoryDomainEvent
{
    TNew Upcast(TOld old);
}
