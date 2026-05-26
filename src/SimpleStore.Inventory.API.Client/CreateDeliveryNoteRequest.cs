namespace SimpleStore.Inventory.API.Client;

// POST body for /api/inventory/delivery-notes.
// Id is client-supplied so retries are idempotent at the event store (NoStream
// append rejects duplicate creates). Date is the business date; if omitted the
// server uses "today at midnight UTC".
public class CreateDeliveryNoteRequest
{
    public Guid Id { get; set; }
    public DateTime? Date { get; set; }
    public string? Reference { get; set; }
    public List<InventoryLineDto> Lines { get; set; } = [];
}
