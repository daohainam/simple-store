namespace SimpleStore.Inventory.API.Data.ReadModels;

public class ReceiptNoteLineRow
{
    public Guid ReceiptNoteId { get; set; }
    public int LineNumber { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
