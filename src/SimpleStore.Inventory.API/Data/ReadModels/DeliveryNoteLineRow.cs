namespace SimpleStore.Inventory.API.Data.ReadModels;

// One row per (delivery_note, product) on the issued note.
// Composite primary key (DeliveryNoteId, LineNumber) communicates that lines
// have no independent identity outside their note.
// ProductId is a SOFT REFERENCE to Catalog.Products — no FK, no JOIN.
public class DeliveryNoteLineRow
{
    public Guid DeliveryNoteId { get; set; }
    public int LineNumber { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
