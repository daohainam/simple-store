namespace SimpleStore.Inventory.API.Data.ReadModels;

// Read-model row (table delivery_notes). NOT an aggregate — projected from
// DeliveryNoteIssuedV1 events. LineCount and TotalQuantity are denormalized
// for fast list rendering without a JOIN to the lines table.
public class DeliveryNoteRow
{
    public Guid Id { get; set; }
    public DateTime Date { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public int LineCount { get; set; }
    public int TotalQuantity { get; set; }

    public List<DeliveryNoteLineRow> Lines { get; set; } = [];
}
