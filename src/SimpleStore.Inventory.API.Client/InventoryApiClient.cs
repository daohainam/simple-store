using System.Net;
using System.Net.Http.Json;

namespace SimpleStore.Inventory.API.Client;

public class InventoryApiClient : IInventoryApiClient
{
    private readonly HttpClient _http;

    public InventoryApiClient(HttpClient http) => _http = http;

    public async Task<DeliveryNoteDto> CreateDeliveryNoteAsync(CreateDeliveryNoteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/inventory/delivery-notes", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeliveryNoteDto>(cancellationToken))!;
    }

    public async Task<DeliveryNoteDto?> GetDeliveryNoteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/inventory/delivery-notes/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeliveryNoteDto>(cancellationToken);
    }

    public async Task<PagedResult<DeliveryNoteDto>> GetDeliveryNotesAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<DeliveryNoteDto>>(
            $"api/v1/inventory/delivery-notes?page={page}&pageSize={pageSize}",
            cancellationToken);
        return result ?? new PagedResult<DeliveryNoteDto> { Page = page, PageSize = pageSize };
    }

    public async Task<ReceiptNoteDto> CreateReceiptNoteAsync(CreateReceiptNoteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/inventory/receipt-notes", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReceiptNoteDto>(cancellationToken))!;
    }

    public async Task<ReceiptNoteDto?> GetReceiptNoteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/inventory/receipt-notes/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReceiptNoteDto>(cancellationToken);
    }

    public async Task<PagedResult<ReceiptNoteDto>> GetReceiptNotesAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<ReceiptNoteDto>>(
            $"api/v1/inventory/receipt-notes?page={page}&pageSize={pageSize}",
            cancellationToken);
        return result ?? new PagedResult<ReceiptNoteDto> { Page = page, PageSize = pageSize };
    }

    public async Task<PagedResult<StockLevelDto>> GetStockLevelsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<StockLevelDto>>(
            $"api/v1/inventory/stock?page={page}&pageSize={pageSize}",
            cancellationToken);
        return result ?? new PagedResult<StockLevelDto> { Page = page, PageSize = pageSize };
    }

    public async Task<StockLevelDto?> GetStockLevelAsync(int productId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/inventory/stock/{productId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StockLevelDto>(cancellationToken);
    }

    public async Task<PagedResult<StockMovementDto>> GetStockMovementsAsync(int productId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<StockMovementDto>>(
            $"api/v1/inventory/stock/{productId}/movements?page={page}&pageSize={pageSize}",
            cancellationToken);
        return result ?? new PagedResult<StockMovementDto> { Page = page, PageSize = pageSize };
    }
}
