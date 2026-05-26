namespace SimpleStore.Inventory.API.Client;

// Wire shape for one line on a delivery or receipt note. ProductId is a soft
// reference to Catalog.Products.Id (no FK; Catalog and Inventory are separate
// bounded contexts).
public class InventoryLineDto
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
