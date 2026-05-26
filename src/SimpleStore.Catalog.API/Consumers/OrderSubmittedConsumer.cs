using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Catalog.API.Data;
using SimpleStore.Contracts;

namespace SimpleStore.Catalog.API.Consumers;

/// <summary>
/// Decrements Product.Stock for every line item in a freshly submitted order.
///
/// The MassTransit EF Core inbox (configured in Program.cs) guards against duplicate delivery,
/// so this consumer can assume each OrderSubmittedEvent is applied exactly once.
///
/// Stock is allowed to go negative — current Catalog write endpoints don't validate stock either,
/// and the model treats it as a signal to operations rather than a hard invariant.
/// </summary>
public class OrderSubmittedConsumer : IConsumer<OrderSubmittedEvent>
{
    private readonly CatalogDbContext _context;
    private readonly ILogger<OrderSubmittedConsumer> _logger;

    public OrderSubmittedConsumer(CatalogDbContext context, ILogger<OrderSubmittedConsumer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderSubmittedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        var productIds = evt.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _context.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var item in evt.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                _logger.LogWarning(
                    "OrderSubmittedEvent (OrderId={OrderId}) referenced unknown ProductId={ProductId} — skipping stock decrement.",
                    evt.OrderId, item.ProductId);
                continue;
            }

            product.Stock -= item.Quantity;
        }

        await _context.SaveChangesAsync(ct);
    }
}
