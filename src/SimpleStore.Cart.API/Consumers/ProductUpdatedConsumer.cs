using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using SimpleStore.Cart.API.Client;
using SimpleStore.Cart.API.Services;
using SimpleStore.Contracts;

namespace SimpleStore.Cart.API.Consumers;

/// <summary>
/// Refreshes denormalized cart line items (ProductName, UnitPrice, ImageUrl) for every cart that
/// holds the updated product.
///
/// Cart.API has no DbContext, so MassTransit's EF Core inbox isn't available; we rely on the
/// consumer being idempotent — re-applying the same ProductUpdatedEvent writes identical field
/// values for matching lines, so duplicate delivery is harmless. Quantity and unrelated lines
/// are untouched.
///
/// We scan every cart key via IConnectionMultiplexer.SCAN rather than maintaining a reverse
/// index. This is linear in cart count; acceptable while cart-key count is small/medium.
/// </summary>
public class ProductUpdatedConsumer : IConsumer<ProductUpdatedEvent>
{
    private static readonly DistributedCacheEntryOptions EntryOptions = new()
    {
        SlidingExpiration = TimeSpan.FromDays(30)
    };

    private readonly ICartStore _store;
    private readonly IDistributedCache _cache;
    private readonly ILogger<ProductUpdatedConsumer> _logger;

    public ProductUpdatedConsumer(ICartStore store, IDistributedCache cache, ILogger<ProductUpdatedConsumer> logger)
    {
        _store = store;
        _cache = cache;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProductUpdatedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        var touched = 0;

        // SCAN is used under the hood by IConnectionMultiplexer.EnumerateKeysAsync to avoid blocking Redis.
        // If cart count grows materially, switch to a maintained reverse index (product:{id}:carts) or a similar approach instead.
        await foreach (var ownerKey in _store.EnumerateOwnerKeysAsync(ct))
        {
            var redisKey = "cart:" + ownerKey;
            var raw = await _cache.GetStringAsync(redisKey, ct);
            if (string.IsNullOrEmpty(raw)) continue;

            var items = JsonSerializer.Deserialize<List<CartItemDto>>(raw);
            if (items is null || items.Count == 0) continue;

            var dirty = false;
            foreach (var item in items)
            {
                if (item.ProductId != evt.ProductId) continue;
                item.ProductName = evt.Name;
                item.UnitPrice = evt.Price;
                item.ImageUrl = evt.ImageUrl;
                dirty = true;
            }

            if (dirty)
            {
                await _cache.SetStringAsync(redisKey, JsonSerializer.Serialize(items), EntryOptions, ct);
                touched++;
            }
        }

        if (touched > 0)
        {
            _logger.LogInformation(
                "Refreshed {Count} cart(s) after ProductUpdatedEvent for ProductId={ProductId}.",
                touched, evt.ProductId);
        }
    }
}
