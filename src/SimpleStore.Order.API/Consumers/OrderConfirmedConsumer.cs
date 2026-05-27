using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Order.API.Data;

namespace SimpleStore.Order.API.Consumers;

/// <summary>
/// Consumed when the checkout saga finishes successfully. Flips Order.Status to "Confirmed"
/// on the row matching CorrelationId. The MassTransit EF inbox makes the consume idempotent.
/// </summary>
public sealed class OrderConfirmedConsumer : IConsumer<OrderConfirmedEvent>
{
    private readonly OrderDbContext _context;
    private readonly ILogger<OrderConfirmedConsumer> _log;

    public OrderConfirmedConsumer(OrderDbContext context, ILogger<OrderConfirmedConsumer> log)
    {
        _context = context;
        _log = log;
    }

    public async Task Consume(ConsumeContext<OrderConfirmedEvent> context)
    {
        var msg = context.Message;
        var order = await _context.Orders.FirstOrDefaultAsync(
            o => o.CorrelationId == msg.CorrelationId, context.CancellationToken);

        if (order is null)
        {
            _log.LogWarning("OrderConfirmedEvent for unknown CorrelationId {CorrelationId}", msg.CorrelationId);
            return;
        }

        order.Status = "Confirmed";
        await _context.SaveChangesAsync(context.CancellationToken);
    }
}
