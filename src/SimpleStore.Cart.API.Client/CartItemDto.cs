namespace SimpleStore.Cart.API.Client;

public class CartItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public decimal TotalPrice => UnitPrice * Quantity;
}
