namespace SimpleStore.Inventory.API.Data.ReadModels;

// Read-model row (table reservations). NOT an aggregate — projected from StockReservedV1 events.
// Status is always "Active" in v8; v9 adds "Committed" / "Cancelled". LineCount and TotalQuantity
// are denormalized for fast list rendering without a JOIN to the lines table.
public class ReservationRow
{
    public Guid Id { get; set; }
    public int OrderId { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
    public string Status { get; set; } = "Active";
    public int LineCount { get; set; }
    public int TotalQuantity { get; set; }

    public List<ReservationLineRow> Lines { get; set; } = [];
}
