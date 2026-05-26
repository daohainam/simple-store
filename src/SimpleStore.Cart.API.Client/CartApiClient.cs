using System.Net.Http.Json;

namespace SimpleStore.Cart.API.Client;

public class CartApiClient : ICartApiClient
{
    private readonly HttpClient _http;

    public CartApiClient(HttpClient http) => _http = http;

    public async Task<CartDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<CartDto>("api/v1/cart", cancellationToken);
        return result ?? new CartDto();
    }

    public async Task<CartDto> AddItemAsync(AddCartItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/cart/items", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CartDto>(cancellationToken))!;
    }

    public async Task<CartDto> UpdateItemAsync(int productId, int quantity, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(
            $"api/v1/cart/items/{productId}",
            new UpdateCartItemRequest { Quantity = quantity },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CartDto>(cancellationToken))!;
    }

    public async Task<CartDto> RemoveItemAsync(int productId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/v1/cart/items/{productId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CartDto>(cancellationToken))!;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync("api/v1/cart", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<int>("api/v1/cart/count", cancellationToken);

    public async Task<decimal> GetTotalAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<decimal>("api/v1/cart/total", cancellationToken);

    public async Task MergeAsync(string anonymousCartId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/v1/cart/merge",
            new MergeCartRequest { AnonymousCartId = anonymousCartId },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
