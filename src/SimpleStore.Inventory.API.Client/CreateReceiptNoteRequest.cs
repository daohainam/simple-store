namespace SimpleStore.Inventory.API.Client;

// POST body for /api/inventory/receipt-notes. Mirror of CreateDeliveryNoteRequest.
public class CreateReceiptNoteRequest
{
    public Guid Id { get; set; }
    public DateTime? Date { get; set; }
    public string? Reference { get; set; }
    public List<InventoryLineDto> Lines { get; set; } = [];
}
