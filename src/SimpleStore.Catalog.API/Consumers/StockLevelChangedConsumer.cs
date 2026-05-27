using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Catalog.API.Data;
using SimpleStore.Contracts;

namespace SimpleStore.Catalog.API.Consumers;

/// <summary>
/// Refreshes the denormalized Product.Stock cache from Inventory.API (the single source of truth
/// for stock in v8+). Inventory's projector publishes StockLevelChangedEvent whenever stock_levels
/// changes (reservations, receipt notes, delivery notes); we overwrite Product.Stock with the
/// authoritative NewOnHand.
///
/// Idempotent: writing the same NewOnHand twice is harmless, and the MassTransit EF inbox guards
/// against duplicate delivery. Eventual consistency: the storefront may briefly show stale stock
/// between the inventory change and this consume — acceptable for the sample.
/// </summary>
public sealed class StockLevelChangedConsumer : IConsumer<StockLevelChangedEvent>
{
    private readonly CatalogDbContext _context;
    private readonly ILogger<StockLevelChangedConsumer> _logger;

    public StockLevelChangedConsumer(CatalogDbContext context, ILogger<StockLevelChangedConsumer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<StockLevelChangedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == msg.ProductId, ct);
        if (product is null)
        {
            _logger.LogWarning(
                "StockLevelChangedEvent for unknown ProductId={ProductId} — Catalog has no such product.",
                msg.ProductId);
            return;
        }

        product.Stock = msg.NewOnHand;
        await _context.SaveChangesAsync(ct);
    }
}
