using System.Text.Json;
using SimpleStore.Catalog.API.Client;

namespace SimpleStore.Web.Services;

public class CartService : ICartService
{
    private const string CartKey = "shopping_cart";
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ICatalogApiClient _catalog;

    public CartService(IHttpContextAccessor httpContextAccessor, ICatalogApiClient catalog)
    {
        _httpContextAccessor = httpContextAccessor;
        _catalog = catalog;
    }

    private List<CartItem> GetCartFromSession()
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        var cartJson = session?.GetString(CartKey);
        return cartJson == null ? new List<CartItem>() : JsonSerializer.Deserialize<List<CartItem>>(cartJson) ?? new List<CartItem>();
    }

    private void SaveCartToSession(List<CartItem> items)
    {
        var session = _httpContextAccessor.HttpContext?.Session;
        session?.SetString(CartKey, JsonSerializer.Serialize(items));
    }

    public Task<List<CartItem>> GetCartItemsAsync() => Task.FromResult(GetCartFromSession());

    public async Task AddToCartAsync(int productId, int quantity = 1)
    {
        var cart = GetCartFromSession();
        var existing = cart.FirstOrDefault(i => i.ProductId == productId);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            var product = await _catalog.GetProductByIdAsync(productId);
            if (product != null)
            {
                cart.Add(new CartItem
                {
                    ProductId = productId,
                    ProductName = product.Name,
                    UnitPrice = product.Price,
                    Quantity = quantity,
                    ImageUrl = product.ImageUrl
                });
            }
        }
        SaveCartToSession(cart);
    }

    public Task UpdateQuantityAsync(int productId, int quantity)
    {
        var cart = GetCartFromSession();
        var item = cart.FirstOrDefault(i => i.ProductId == productId);
        if (item != null)
        {
            if (quantity <= 0)
                cart.Remove(item);
            else
                item.Quantity = quantity;
        }
        SaveCartToSession(cart);
        return Task.CompletedTask;
    }

    public Task RemoveFromCartAsync(int productId)
    {
        var cart = GetCartFromSession();
        cart.RemoveAll(i => i.ProductId == productId);
        SaveCartToSession(cart);
        return Task.CompletedTask;
    }

    public Task ClearCartAsync()
    {
        SaveCartToSession(new List<CartItem>());
        return Task.CompletedTask;
    }

    public Task<int> GetCartCountAsync()
    {
        var cart = GetCartFromSession();
        return Task.FromResult(cart.Sum(i => i.Quantity));
    }

    public Task<decimal> GetCartTotalAsync()
    {
        var cart = GetCartFromSession();
        return Task.FromResult(cart.Sum(i => i.TotalPrice));
    }
}
