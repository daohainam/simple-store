using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using SimpleStore.Cart.API.Client;
using SimpleStore.Cart.API.Observability;
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
///
/// v10: hot-path logging uses LoggerMessage source generation (compile-time formatter, no
/// boxing on the happy path) because this consumer runs on every admin product edit and the
/// fan-out can touch thousands of cart keys. The CartFanoutDuration histogram records how long
/// the scan takes so operators can spot when key count grows past the "small/medium" comfort
/// zone (the threshold where a reverse index becomes worth maintaining).
/// </summary>
public partial class ProductUpdatedConsumer : IConsumer<ProductUpdatedEvent>
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

        // v10: scope every log line in this consume by ProductId so dashboard log filters trivially
        // collate every cart-refresh attempt for one product. No CorrelationId here — the event is
        // a Catalog domain notification, not saga-coupled.
        using var _ = _logger.BeginScope(new Dictionary<string, object> { ["ProductId"] = evt.ProductId });

        var touched = 0;
        var scanned = 0;
        var sw = Stopwatch.StartNew();

        // SCAN is used under the hood by IConnectionMultiplexer.EnumerateKeysAsync to avoid blocking Redis.
        // If cart count grows materially, switch to a maintained reverse index (product:{id}:carts) or a similar approach instead.
        await foreach (var ownerKey in _store.EnumerateOwnerKeysAsync(ct))
        {
            scanned++;
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

        sw.Stop();

        // Record the duration regardless of touch count — the cost is the SCAN, not the writes.
        Telemetry.CartFanoutDuration.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("scanned", scanned),
            new KeyValuePair<string, object?>("touched", touched));

        if (touched > 0)
        {
            LogFanoutTouched(_logger, touched, scanned, evt.ProductId, sw.Elapsed.TotalMilliseconds);
        }
    }

    // v10: high-performance source-generated logger. The formatter is generated at compile time;
    // the call site uses no boxing and no reflection. Worth it because the fan-out consumer fires
    // on every product edit and `touched > 0` is the common case in a non-empty store.
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Refreshed {Touched} cart(s) of {Scanned} scanned after ProductUpdatedEvent for ProductId={ProductId} in {ElapsedMs:F1} ms.")]
    private static partial void LogFanoutTouched(
        ILogger logger, int touched, int scanned, int productId, double elapsedMs);
}
