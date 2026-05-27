using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Order.API.Data;

namespace SimpleStore.Order.API.Consumers;

/// <summary>
/// Consumed when the checkout saga fails (insufficient stock) or times out. Flips Order.Status
/// to "Cancelled" on the row matching CorrelationId. The reason is logged for diagnostics.
/// </summary>
public sealed class OrderCancelledConsumer : IConsumer<OrderCancelledEvent>
{
    private readonly OrderDbContext _context;
    private readonly ILogger<OrderCancelledConsumer> _log;

    public OrderCancelledConsumer(OrderDbContext context, ILogger<OrderCancelledConsumer> log)
    {
        _context = context;
        _log = log;
    }

    public async Task Consume(ConsumeContext<OrderCancelledEvent> context)
    {
        var msg = context.Message;
        var order = await _context.Orders.FirstOrDefaultAsync(
            o => o.CorrelationId == msg.CorrelationId, context.CancellationToken);

        if (order is null)
        {
            _log.LogWarning("OrderCancelledEvent for unknown CorrelationId {CorrelationId}", msg.CorrelationId);
            return;
        }

        order.Status = "Cancelled";
        await _context.SaveChangesAsync(context.CancellationToken);
        _log.LogInformation("Order {OrderId} cancelled. Reason: {Reason}", order.Id, msg.Reason);
    }
}
