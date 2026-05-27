using SimpleStore.Catalog.API.Client;

namespace SimpleStore.Catalog.API.Services;

public interface ICatalogService
{
    // Reads
    Task<PagedResult<ProductDto>> GetProductsAsync(int page, int pageSize, int? categoryId, string? searchTerm, CancellationToken ct = default);
    Task<ProductDto?> GetProductByIdAsync(int id, CancellationToken ct = default);
    Task<int> GetProductCountAsync(CancellationToken ct = default);

    Task<PagedResult<CategoryDto>> GetCategoriesAsync(int page, int pageSize, CancellationToken ct = default);
    Task<CategoryDto?> GetCategoryByIdAsync(int id, CancellationToken ct = default);
    Task<int> GetCategoryCountAsync(CancellationToken ct = default);

    // Writes. Stock is NOT settable here — Inventory.API owns it (v8+).
    Task<ProductDto> CreateProductAsync(CreateProductRequest request, CancellationToken ct = default);
    /// <summary>Returns false if the product was not found, true on success.</summary>
    Task<bool> UpdateProductAsync(int id, UpdateProductRequest request, CancellationToken ct = default);
    Task<bool> DeleteProductAsync(int id, CancellationToken ct = default);

    Task<CategoryDto> CreateCategoryAsync(CategoryDto dto, CancellationToken ct = default);
    Task<bool> UpdateCategoryAsync(int id, CategoryDto dto, CancellationToken ct = default);
    Task<DeleteCategoryResult> DeleteCategoryAsync(int id, CancellationToken ct = default);
}

public enum DeleteCategoryResult
{
    NotFound,
    HasProducts,
    Deleted
}
