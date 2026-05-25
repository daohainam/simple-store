namespace SimpleStore.Catalog.API.Client;

public interface ICatalogApiClient
{
    // Reads
    Task<PagedResult<ProductDto>> GetProductsAsync(
        int page = 1,
        int pageSize = 20,
        int? categoryId = null,
        string? search = null,
        CancellationToken cancellationToken = default);

    Task<ProductDto?> GetProductByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<int> GetProductCountAsync(CancellationToken cancellationToken = default);

    Task<PagedResult<CategoryDto>> GetCategoriesAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<CategoryDto?> GetCategoryByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<int> GetCategoryCountAsync(CancellationToken cancellationToken = default);

    // Writes
    Task<ProductDto> CreateProductAsync(ProductDto product, CancellationToken cancellationToken = default);
    Task UpdateProductAsync(int id, ProductDto product, CancellationToken cancellationToken = default);
    Task DeleteProductAsync(int id, CancellationToken cancellationToken = default);

    Task<CategoryDto> CreateCategoryAsync(CategoryDto category, CancellationToken cancellationToken = default);
    Task UpdateCategoryAsync(int id, CategoryDto category, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(int id, CancellationToken cancellationToken = default);
}
