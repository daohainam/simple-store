namespace SimpleStore.Inventory.API.Domain.Shared;

// DDD value object: structural equality, immutable, no identity of its own.
// Lives inside DeliveryNote / ReceiptNote aggregates.
// Quantity > 0 invariant is enforced at construction.
public sealed record InventoryLine
{
    public int ProductId { get; }
    public int Quantity { get; }

    public InventoryLine(int productId, int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException(
                $"Inventory line quantity must be positive (got {quantity} for productId {productId}). " +
                "A negative quantity on a delivery note is a receipt note — use the right document type.");
        }
        ProductId = productId;
        Quantity = quantity;
    }
}
