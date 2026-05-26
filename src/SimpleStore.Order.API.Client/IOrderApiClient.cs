namespace SimpleStore.Order.API.Client;

public interface IOrderApiClient
{
    // Storefront (current user) — owner enforced by sub claim server-side.
    Task<IReadOnlyList<OrderDto>> GetMyOrdersAsync(CancellationToken cancellationToken = default);
    Task<OrderDto?> GetMyOrderByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<OrderDto> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);

    // Admin — gated by the "Admin" policy on the server.
    Task<PagedResult<OrderDto>> GetOrdersAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<int> GetOrderCountAsync(CancellationToken cancellationToken = default);
    Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default);
    Task UpdateOrderStatusAsync(int id, string status, CancellationToken cancellationToken = default);
    Task<OrderStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, int>> GetOrderCountsByUserAsync(CancellationToken cancellationToken = default);
}
