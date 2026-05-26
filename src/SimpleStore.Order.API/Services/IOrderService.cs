using SimpleStore.Order.API.Client;

namespace SimpleStore.Order.API.Services;

public interface IOrderService
{
    // Storefront
    Task<IReadOnlyList<OrderDto>> GetMyOrdersAsync(string userId, CancellationToken ct = default);
    Task<OrderDto?> GetMyOrderByIdAsync(int id, string userId, CancellationToken ct = default);
    Task<OrderDto> CreateOrderAsync(string userId, CreateOrderRequest request, CancellationToken ct = default);

    // Admin
    Task<PagedResult<OrderDto>> GetOrdersAsync(int page, int pageSize, CancellationToken ct = default);
    Task<int> GetOrderCountAsync(CancellationToken ct = default);
    Task<OrderDto?> GetOrderByIdAsync(int id, CancellationToken ct = default);
    /// <summary>Returns false if the order was not found, true on success.</summary>
    Task<bool> UpdateStatusAsync(int id, string status, CancellationToken ct = default);
    Task<OrderStatsDto> GetStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetOrderCountsByUserAsync(CancellationToken ct = default);
}
