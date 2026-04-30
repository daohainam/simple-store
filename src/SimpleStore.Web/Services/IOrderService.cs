using SimpleStore.Data.Models;
namespace SimpleStore.Web.Services;

public interface IOrderService
{
    Task<Order> CreateOrderAsync(string userId, string shippingAddress, List<CartItem> cartItems);
    Task<IEnumerable<Order>> GetUserOrdersAsync(string userId);
    Task<Order?> GetOrderByIdAsync(int orderId, string userId);
}
