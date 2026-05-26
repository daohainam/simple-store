namespace SimpleStore.Cart.API.Client;

public interface ICartApiClient
{
    Task<CartDto> GetAsync(CancellationToken cancellationToken = default);
    Task<CartDto> AddItemAsync(AddCartItemRequest request, CancellationToken cancellationToken = default);
    Task<CartDto> UpdateItemAsync(int productId, int quantity, CancellationToken cancellationToken = default);
    Task<CartDto> RemoveItemAsync(int productId, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<int> GetCountAsync(CancellationToken cancellationToken = default);
    Task<decimal> GetTotalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds the anonymous cart at <paramref name="anonymousCartId"/> into the current user's cart.
    /// Requires a JWT bearer token; the destination owner comes from the "sub" claim.
    /// </summary>
    Task MergeAsync(string anonymousCartId, CancellationToken cancellationToken = default);
}
