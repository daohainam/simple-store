namespace SimpleStore.Inventory.API.Data.ReadModels;

// One row per product, tracking current stock-on-hand.
//
// This row is a CACHE. The ledger (stock_movements) is itself a cache.
// The truth lives in the event store as the sequence of DeliveryNoteIssuedV1
// and ReceiptNoteRecordedV1 events. Wipe both Postgres tables and the
// projector will rebuild them by replaying the event store from the start.
//
// OnHand is allowed to go negative (matches Catalog's existing posture).
public class StockLevelRow
{
    public int ProductId { get; set; }
    public int OnHand { get; set; }
    public DateTimeOffset LastMovementAt { get; set; }
}
