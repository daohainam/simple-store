using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Order.API.Data;
using SimpleStore.Order.API.Models;
using SimpleStore.Order.API.Observability;

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

        // v10: scope by CorrelationId so this log joins the rest of the saga's audit trail.
        using var _ = _log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = msg.CorrelationId });

        var order = await _context.Orders.FirstOrDefaultAsync(
            o => o.CorrelationId == msg.CorrelationId, context.CancellationToken);

        if (order is null)
        {
            _log.LogWarning("OrderCancelledEvent for unknown CorrelationId {CorrelationId}", msg.CorrelationId);
            return;
        }

        order.Status = OrderStatus.Cancelled;
        await _context.SaveChangesAsync(context.CancellationToken);

        // v10: split the counter by reason so the dashboard can show "ReservationTimeout vs InsufficientStock vs ..."
        Telemetry.OrdersCancelled.Add(1, new KeyValuePair<string, object?>("reason", msg.Reason ?? "Unknown"));

        _log.LogInformation("Order {OrderId} cancelled. Reason: {Reason}", order.Id, msg.Reason);
    }
}
