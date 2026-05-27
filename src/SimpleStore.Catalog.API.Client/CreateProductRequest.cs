namespace SimpleStore.Catalog.API.Client;

// Write model for creating a product. Deliberately has NO Stock field — in v8+ Inventory.API is the
// single source of truth for stock. Initial stock is established by issuing a receipt note in
// Inventory; Catalog's Product.Stock is a read-only cache refreshed via StockLevelChangedEvent.
public class CreateProductRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public int CategoryId { get; set; }
}
