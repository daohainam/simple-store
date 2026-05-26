namespace SimpleStore.Contracts;

/// <summary>
/// Published by Catalog.API after a product is updated. Cart.API consumes this to refresh
/// the denormalized ProductName / UnitPrice / ImageUrl on any cart line that holds this product.
/// Fields mirror ProductDto so a consumer can refresh every denormalized copy in one pass.
/// </summary>
public sealed record ProductUpdatedEvent
{
    public int ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public int Stock { get; init; }
    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
}
