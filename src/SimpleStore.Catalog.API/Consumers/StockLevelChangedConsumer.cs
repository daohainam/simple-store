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

        // v10: scope by ProductId + cause. StockLevelChanged carries no CorrelationId (it's a
        // domain-event-derived integration event, not saga-coupled), but ProductId is what an
        // operator filters on when triaging "why did this product's stock jump?"
        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ProductId"] = msg.ProductId,
            ["StockChangeCause"] = msg.Cause
        });

        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == msg.ProductId, ct);
        if (product is null)
        {
            _logger.LogWarning(
                "StockLevelChangedEvent for unknown ProductId={ProductId} — Catalog has no such product.",
                msg.ProductId);
            return;
        }

        var oldStock = product.Stock;
        product.Stock = msg.NewOnHand;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Product {ProductId} stock updated {OldStock} → {NewStock} (cause: {Cause})",
            product.Id, oldStock, msg.NewOnHand, msg.Cause);
    }
}
