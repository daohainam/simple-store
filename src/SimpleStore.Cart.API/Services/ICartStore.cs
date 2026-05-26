using SimpleStore.Cart.API.Client;

namespace SimpleStore.Cart.API.Services;

public interface ICartStore
{
    Task<CartDto> GetAsync(string ownerKey, CancellationToken ct = default);
    Task<CartDto> AddItemAsync(string ownerKey, AddCartItemRequest request, CancellationToken ct = default);
    Task<CartDto> UpdateItemAsync(string ownerKey, int productId, int quantity, CancellationToken ct = default);
    Task<CartDto> RemoveItemAsync(string ownerKey, int productId, CancellationToken ct = default);
    Task ClearAsync(string ownerKey, CancellationToken ct = default);

    /// <summary>
    /// Merges items from <paramref name="fromKey"/> into <paramref name="toKey"/> (summing quantities by productId)
    /// and removes the source cart. No-op if the source cart is empty or missing.
    /// </summary>
    Task MergeAsync(string fromKey, string toKey, CancellationToken ct = default);

    /// <summary>
    /// Enumerates every cart owner key currently stored. SCAN-based fan-out used by event consumers
    /// that need to touch carts containing a given product (see ProductUpdatedConsumer).
    /// </summary>
    IAsyncEnumerable<string> EnumerateOwnerKeysAsync(CancellationToken ct = default);
}
