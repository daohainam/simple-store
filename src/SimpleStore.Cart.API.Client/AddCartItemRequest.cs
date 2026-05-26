namespace SimpleStore.Cart.API.Client;

public class AddCartItemRequest
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}
