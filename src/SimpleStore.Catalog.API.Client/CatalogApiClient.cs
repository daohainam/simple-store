using System.Net;
using System.Net.Http.Json;

namespace SimpleStore.Catalog.API.Client;

public class CatalogApiClient : ICatalogApiClient
{
    private readonly HttpClient _http;

    public CatalogApiClient(HttpClient http) => _http = http;

    public async Task<PagedResult<ProductDto>> GetProductsAsync(
        int page = 1,
        int pageSize = 20,
        int? categoryId = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"api/catalog/products?page={page}&pageSize={pageSize}";
        if (categoryId.HasValue) query += $"&categoryId={categoryId.Value}";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search)}";

        var result = await _http.GetFromJsonAsync<PagedResult<ProductDto>>(query, cancellationToken);
        return result ?? new PagedResult<ProductDto> { Page = page, PageSize = pageSize };
    }

    public async Task<ProductDto?> GetProductByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/catalog/products/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProductDto>(cancellationToken);
    }

    public async Task<int> GetProductCountAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<int>("api/catalog/products/count", cancellationToken);

    public async Task<PagedResult<CategoryDto>> GetCategoriesAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<CategoryDto>>(
            $"api/catalog/categories?page={page}&pageSize={pageSize}",
            cancellationToken);
        return result ?? new PagedResult<CategoryDto> { Page = page, PageSize = pageSize };
    }

    public async Task<CategoryDto?> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/catalog/categories/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken);
    }

    public async Task<int> GetCategoryCountAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<int>("api/catalog/categories/count", cancellationToken);

    public async Task<ProductDto> CreateProductAsync(ProductDto product, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/catalog/products", product, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductDto>(cancellationToken))!;
    }

    public async Task UpdateProductAsync(int id, ProductDto product, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/catalog/products/{id}", product, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProductAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/catalog/products/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<CategoryDto> CreateCategoryAsync(CategoryDto category, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/catalog/categories", category, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(cancellationToken))!;
    }

    public async Task UpdateCategoryAsync(int id, CategoryDto category, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/catalog/categories/{id}", category, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/catalog/categories/{id}", cancellationToken);
        // 409 Conflict bubbles up — caller surfaces a message to the user.
        response.EnsureSuccessStatusCode();
    }
}
