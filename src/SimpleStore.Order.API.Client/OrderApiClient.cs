using System.Net;
using System.Net.Http.Json;

namespace SimpleStore.Order.API.Client;

public class OrderApiClient : IOrderApiClient
{
    private readonly HttpClient _http;

    public OrderApiClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<OrderDto>> GetMyOrdersAsync(CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<OrderDto>>("api/v1/order/orders", cancellationToken);
        return result ?? new List<OrderDto>();
    }

    public async Task<OrderDto?> GetMyOrderByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/order/orders/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken);
    }

    public async Task<OrderDto> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/order/orders", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken))!;
    }

    public async Task<PagedResult<OrderDto>> GetOrdersAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<OrderDto>>(
            $"api/v1/order/admin/orders?page={page}&pageSize={pageSize}",
            cancellationToken);
        return result ?? new PagedResult<OrderDto> { Page = page, PageSize = pageSize };
    }

    public async Task<int> GetOrderCountAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<int>("api/v1/order/admin/orders/count", cancellationToken);

    public async Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/order/admin/orders/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken);
    }

    public async Task UpdateOrderStatusAsync(int id, string status, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PatchAsJsonAsync(
            $"api/v1/order/admin/orders/{id}/status",
            new UpdateOrderStatusRequest { Status = status },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<OrderStatsDto> GetStatsAsync(CancellationToken cancellationToken = default) =>
        (await _http.GetFromJsonAsync<OrderStatsDto>("api/v1/order/admin/stats", cancellationToken))
            ?? new OrderStatsDto();

    public async Task<IReadOnlyDictionary<string, int>> GetOrderCountsByUserAsync(CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<Dictionary<string, int>>(
            "api/v1/order/admin/orders/counts-by-user", cancellationToken);
        return result ?? new Dictionary<string, int>();
    }
}
