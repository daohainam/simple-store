using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Order.API.Client;
using SimpleStore.Order.API.Data;
using SimpleStore.Order.API.Models;
using OrderEntity = SimpleStore.Order.API.Models.Order;
using OrderItem = SimpleStore.Order.API.Models.OrderItem;

namespace SimpleStore.Order.API.Services;

public class OrderService : IOrderService
{
    private const int MaxPageSize = 100;

    private readonly OrderDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;

    public OrderService(OrderDbContext context, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
    }

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
            CorrelationId = Guid.NewGuid(),
            UserId = userId,
            OrderDate = DateTime.UtcNow,
            ShippingAddress = request.ShippingAddress,
            Status = OrderStatus.Pending,
            TotalAmount = request.Items.Sum(i => i.UnitPrice * i.Quantity),
            Items = request.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        // Insert the order first so EF assigns Id values to it and its OrderItems; that Id is what
        // OrderSubmittedEvent carries to downstream consumers. The second SaveChangesAsync flushes
        // the in-memory bus outbox into OutboxMessage. We wrap both in an explicit transaction so
        // a crash between them cannot leave the order persisted without its event queued.
        //
        // v9: the entire transaction runs inside an IExecutionStrategy so EF Core's retry-on-failure
        // (configured in Program.cs) can replay the whole unit of work on a transient Postgres error
        // — required because EnableRetryOnFailure forbids user-initiated transactions outside the
        // strategy. The lambda is idempotent: EF re-issues SaveChanges with new identity values on
        // each attempt, and the outbox row commits atomically with the order row.
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            _context.Orders.Add(order);
            await _context.SaveChangesAsync(ct);

            await _publishEndpoint.Publish(new OrderSubmittedEvent
            {
                CorrelationId = order.CorrelationId,
                OrderId = order.Id,
                UserId = order.UserId,
                OrderDate = order.OrderDate,
                TotalAmount = order.TotalAmount,
                ShippingAddress = order.ShippingAddress,
                Items = order.Items.Select(i => new OrderSubmittedLineItem
                {
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList()
            }, ct);

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

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
        if (!Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed))
            return false;
        var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return false;
        order.Status = parsed;
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
                PendingCount = g.Sum(o => o.Status == OrderStatus.Pending ? 1 : 0),
                ConfirmedCount = g.Sum(o => o.Status == OrderStatus.Confirmed ? 1 : 0),
                CancelledCount = g.Sum(o => o.Status == OrderStatus.Cancelled ? 1 : 0),
                CompletedCount = g.Sum(o => o.Status == OrderStatus.Delivered ? 1 : 0),
                TotalRevenue = g.Sum(o => (decimal?)o.TotalAmount) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        return grouped is null
            ? new OrderStatsDto()
            : new OrderStatsDto
            {
                TotalCount = grouped.TotalCount,
                PendingCount = grouped.PendingCount,
                ConfirmedCount = grouped.ConfirmedCount,
                CancelledCount = grouped.CancelledCount,
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
        Status = o.Status.ToString(),
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
