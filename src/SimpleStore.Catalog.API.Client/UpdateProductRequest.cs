namespace SimpleStore.Catalog.API.Client;

// Write model for updating a product. Deliberately has NO Stock field — in v8+ stock is owned by
// Inventory.API and adjusted there (receipt/delivery notes), never through the Catalog write API.
public class UpdateProductRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public int CategoryId { get; set; }
}
