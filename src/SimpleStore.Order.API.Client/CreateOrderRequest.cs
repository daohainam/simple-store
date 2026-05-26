namespace SimpleStore.Order.API.Client;

public class CreateOrderRequest
{
    public string ShippingAddress { get; set; } = string.Empty;
    public IReadOnlyList<OrderItemDto> Items { get; set; } = Array.Empty<OrderItemDto>();
}
