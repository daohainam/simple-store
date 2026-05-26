namespace SimpleStore.Inventory.API.Client;

public class StockLevelDto
{
    public int ProductId { get; set; }
    public int OnHand { get; set; }
    public DateTimeOffset LastMovementAt { get; set; }
}
