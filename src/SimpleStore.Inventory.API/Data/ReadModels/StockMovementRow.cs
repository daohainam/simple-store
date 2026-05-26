namespace SimpleStore.Inventory.API.Data.ReadModels;

// Append-only audit ledger. One row per (note, line). Delta is signed:
// negative for delivery (OUT), positive for receipt (IN).
public class StockMovementRow
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public int Delta { get; set; }
    public string MovementType { get; set; } = string.Empty; // "DeliveryNote" | "ReceiptNote"
    public Guid SourceNoteId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
