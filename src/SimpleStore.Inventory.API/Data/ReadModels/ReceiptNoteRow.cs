namespace SimpleStore.Inventory.API.Data.ReadModels;

// Mirror of DeliveryNoteRow for stock-IN receipt notes.
public class ReceiptNoteRow
{
    public Guid Id { get; set; }
    public DateTime Date { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public int LineCount { get; set; }
    public int TotalQuantity { get; set; }

    public List<ReceiptNoteLineRow> Lines { get; set; } = [];
}
