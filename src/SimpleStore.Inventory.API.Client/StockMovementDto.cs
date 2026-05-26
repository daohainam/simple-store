namespace SimpleStore.Inventory.API.Client;

public class StockMovementDto
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public int Delta { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public Guid SourceNoteId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
