using Microsoft.EntityFrameworkCore;
using SimpleStore.Data;
using SimpleStore.Data.Models;

namespace SimpleStore.Web.Services;

public class OrderService : IOrderService
{
    private readonly StoreDbContext _context;
    public OrderService(StoreDbContext context) => _context = context;

    public async Task<Order> CreateOrderAsync(string userId, string shippingAddress, List<CartItem> cartItems)
    {
        var order = new Order
        {
            UserId = userId,
            OrderDate = DateTime.UtcNow,
            ShippingAddress = shippingAddress,
            Status = "Pending",
            TotalAmount = cartItems.Sum(i => i.TotalPrice),
            Items = cartItems.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };
        _context.Orders.Add(order);
        await _context.SaveChangesAsync();
        return order;
    }

    public async Task<IEnumerable<Order>> GetUserOrdersAsync(string userId) =>
        await _context.Orders
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.OrderDate)
            .ToListAsync();

    public async Task<Order?> GetOrderByIdAsync(int orderId, string userId) =>
        await _context.Orders
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);
}
