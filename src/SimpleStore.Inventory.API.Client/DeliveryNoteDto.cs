namespace SimpleStore.Inventory.API.Client;

public class DeliveryNoteDto
{
    public Guid Id { get; set; }
    public DateTime Date { get; set; }
    public string? Reference { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public List<InventoryLineDto> Lines { get; set; } = [];
}
