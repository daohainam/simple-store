using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using SimpleStore.Cart.API.Client;

namespace SimpleStore.Cart.API.Services;

public class RedisCartStore : ICartStore
{
    private static readonly DistributedCacheEntryOptions EntryOptions = new()
    {
        SlidingExpiration = TimeSpan.FromDays(30)
    };

    private readonly IDistributedCache _cache;

    public RedisCartStore(IDistributedCache cache) => _cache = cache;

    public async Task<CartDto> GetAsync(string ownerKey, CancellationToken ct = default)
    {
        var items = await LoadItemsAsync(ownerKey, ct);
        return new CartDto { Items = items };
    }

    public async Task<CartDto> AddItemAsync(string ownerKey, AddCartItemRequest request, CancellationToken ct = default)
    {
        var items = await LoadItemsAsync(ownerKey, ct);
        var existing = items.FirstOrDefault(i => i.ProductId == request.ProductId);
        if (existing is null)
        {
            items.Add(new CartItemDto
            {
                ProductId = request.ProductId,
                ProductName = request.ProductName,
                UnitPrice = request.UnitPrice,
                ImageUrl = request.ImageUrl,
                Quantity = Math.Max(1, request.Quantity)
            });
        }
        else
        {
            existing.Quantity += Math.Max(1, request.Quantity);
        }
        await SaveItemsAsync(ownerKey, items, ct);
        return new CartDto { Items = items };
    }

    public async Task<CartDto> UpdateItemAsync(string ownerKey, int productId, int quantity, CancellationToken ct = default)
    {
        var items = await LoadItemsAsync(ownerKey, ct);
        var existing = items.FirstOrDefault(i => i.ProductId == productId);
        if (existing is not null)
        {
            if (quantity <= 0)
            {
                items.Remove(existing);
            }
            else
            {
                existing.Quantity = quantity;
            }
            await SaveItemsAsync(ownerKey, items, ct);
        }
        return new CartDto { Items = items };
    }

    public async Task<CartDto> RemoveItemAsync(string ownerKey, int productId, CancellationToken ct = default)
    {
        var items = await LoadItemsAsync(ownerKey, ct);
        var existing = items.FirstOrDefault(i => i.ProductId == productId);
        if (existing is not null)
        {
            items.Remove(existing);
            await SaveItemsAsync(ownerKey, items, ct);
        }
        return new CartDto { Items = items };
    }

    public Task ClearAsync(string ownerKey, CancellationToken ct = default) =>
        _cache.RemoveAsync(KeyFor(ownerKey), ct);

    public async Task MergeAsync(string fromKey, string toKey, CancellationToken ct = default)
    {
        if (string.Equals(fromKey, toKey, StringComparison.Ordinal)) return;

        var fromItems = await LoadItemsAsync(fromKey, ct);
        if (fromItems.Count == 0)
        {
            await _cache.RemoveAsync(KeyFor(fromKey), ct);
            return;
        }

        var toItems = await LoadItemsAsync(toKey, ct);
        foreach (var src in fromItems)
        {
            var dst = toItems.FirstOrDefault(i => i.ProductId == src.ProductId);
            if (dst is null)
            {
                toItems.Add(src);
            }
            else
            {
                dst.Quantity += src.Quantity;
            }
        }

        await SaveItemsAsync(toKey, toItems, ct);
        await _cache.RemoveAsync(KeyFor(fromKey), ct);
    }

    private async Task<List<CartItemDto>> LoadItemsAsync(string ownerKey, CancellationToken ct)
    {
        var raw = await _cache.GetStringAsync(KeyFor(ownerKey), ct);
        if (string.IsNullOrEmpty(raw)) return new List<CartItemDto>();
        return JsonSerializer.Deserialize<List<CartItemDto>>(raw) ?? new List<CartItemDto>();
    }

    private Task SaveItemsAsync(string ownerKey, List<CartItemDto> items, CancellationToken ct)
    {
        var raw = JsonSerializer.Serialize(items);
        return _cache.SetStringAsync(KeyFor(ownerKey), raw, EntryOptions, ct);
    }

    private static string KeyFor(string ownerKey) => $"cart:{ownerKey}";
}
