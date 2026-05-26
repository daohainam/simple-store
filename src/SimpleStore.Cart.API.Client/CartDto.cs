namespace SimpleStore.Cart.API.Client;

public class CartDto
{
    public IReadOnlyList<CartItemDto> Items { get; set; } = Array.Empty<CartItemDto>();
    public int Count => Items.Sum(i => i.Quantity);
    public decimal Total => Items.Sum(i => i.TotalPrice);
}
