namespace SimpleStore.Inventory.API.Client;

public class ReceiptNoteDto
{
    public Guid Id { get; set; }
    public DateTime Date { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public List<InventoryLineDto> Lines { get; set; } = [];
}
