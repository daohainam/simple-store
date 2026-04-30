namespace SimpleStore.Web.Services;

public interface ICartService
{
    Task<List<CartItem>> GetCartItemsAsync();
    Task AddToCartAsync(int productId, int quantity = 1);
    Task UpdateQuantityAsync(int productId, int quantity);
    Task RemoveFromCartAsync(int productId);
    Task ClearCartAsync();
    Task<int> GetCartCountAsync();
    Task<decimal> GetCartTotalAsync();
}
