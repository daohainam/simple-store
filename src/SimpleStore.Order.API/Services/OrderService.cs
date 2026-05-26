using Microsoft.EntityFrameworkCore;
using SimpleStore.Order.API.Client;
using SimpleStore.Order.API.Data;
using OrderEntity = SimpleStore.Order.API.Models.Order;
using OrderItem = SimpleStore.Order.API.Models.OrderItem;

namespace SimpleStore.Order.API.Services;

public class OrderService : IOrderService
{
    private const int MaxPageSize = 100;

    private readonly OrderDbContext _context;

    public OrderService(OrderDbContext context) => _context = context;

    public async Task<IReadOnlyList<OrderDto>> GetMyOrdersAsync(string userId, CancellationToken ct = default)
    {
        return await _context.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.OrderDate)
            .Select(o => ToDto(o))
            .ToListAsync(ct);
    }

    public async Task<OrderDto?> GetMyOrderByIdAsync(int id, string userId, CancellationToken ct = default)
    {
        var order = await _context.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == userId, ct);
        return order is null ? null : ToDto(order);
    }

    public async Task<OrderDto> CreateOrderAsync(string userId, CreateOrderRequest request, CancellationToken ct = default)
    {
        var order = new OrderEntity
        {
            UserId = userId,
            OrderDate = DateTime.UtcNow,
            ShippingAddress = request.ShippingAddress,
            Status = "Pending",
            TotalAmount = request.Items.Sum(i => i.UnitPrice * i.Quantity),
            Items = request.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };
        _context.Orders.Add(order);
        await _context.SaveChangesAsync(ct);
        return ToDto(order);
    }

    public async Task<PagedResult<OrderDto>> GetOrdersAsync(int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = ClampPaging(page, pageSize);

        var query = _context.Orders.AsNoTracking().Include(o => o.Items);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => ToDto(o))
            .ToListAsync(ct);

        return new PagedResult<OrderDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public Task<int> GetOrderCountAsync(CancellationToken ct = default) =>
        _context.Orders.CountAsync(ct);

    public async Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken ct = default)
    {
        var order = await _context.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        return order is null ? null : ToDto(order);
    }

    public async Task<bool> UpdateStatusAsync(int id, string status, CancellationToken ct = default)
    {
        var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return false;
        order.Status = status;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<OrderStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        // Group-by aggregate keeps this to a single round trip.
        var grouped = await _context.Orders
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(),
                PendingCount = g.Sum(o => o.Status == "Pending" ? 1 : 0),
                CompletedCount = g.Sum(o => o.Status == "Delivered" ? 1 : 0),
                TotalRevenue = g.Sum(o => (decimal?)o.TotalAmount) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        return grouped is null
            ? new OrderStatsDto()
            : new OrderStatsDto
            {
                TotalCount = grouped.TotalCount,
                PendingCount = grouped.PendingCount,
                CompletedCount = grouped.CompletedCount,
                TotalRevenue = grouped.TotalRevenue
            };
    }

    public async Task<IReadOnlyDictionary<string, int>> GetOrderCountsByUserAsync(CancellationToken ct = default)
    {
        return await _context.Orders
            .AsNoTracking()
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
    }

    private static (int page, int pageSize) ClampPaging(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;
        return (page, pageSize);
    }

    private static OrderDto ToDto(OrderEntity o) => new()
    {
        Id = o.Id,
        UserId = o.UserId,
        OrderDate = o.OrderDate,
        TotalAmount = o.TotalAmount,
        Status = o.Status,
        ShippingAddress = o.ShippingAddress,
        Items = o.Items.Select(i => new OrderItemDto
        {
            Id = i.Id,
            ProductId = i.ProductId,
            ProductName = i.ProductName,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice
        }).ToList()
    };
}
