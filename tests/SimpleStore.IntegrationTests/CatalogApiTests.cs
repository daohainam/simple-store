using System.Net;
using System.Net.Http.Json;
using SimpleStore.Catalog.API.Client;
using Xunit;

namespace SimpleStore.IntegrationTests;

[Collection(AppHostCollection.Name)]
public class CatalogApiTests
{
    private readonly HttpClient _client;

    public CatalogApiTests(AppHostFixture fixture)
    {
        _client = fixture.CreateHttpClient("catalog");
    }

    // ─── Products: Read ──────────────────────────────────────────────────

    [Fact]
    public async Task GetProducts_ReturnsSeededProducts()
    {
        var response = await _client.GetAsync("/api/v1/catalog/products");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<ProductDto>>();

        Assert.NotNull(result);
        Assert.True(result.Items.Count > 0, "Seeded products should be present.");
        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task GetProducts_WithPagination_RespectsPageSize()
    {
        var response = await _client.GetAsync("/api/v1/catalog/products?page=1&pageSize=2");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<ProductDto>>();

        Assert.NotNull(result);
        Assert.True(result.Items.Count <= 2);
        Assert.Equal(1, result.Page);
        Assert.Equal(2, result.PageSize);
    }

    [Fact]
    public async Task GetProducts_FilterByCategory_ReturnsMatchingProducts()
    {
        // First get categories to find a valid categoryId
        var categoriesResponse = await _client.GetAsync("/api/v1/catalog/categories");
        categoriesResponse.EnsureSuccessStatusCode();
        var categories = await categoriesResponse.Content.ReadFromJsonAsync<PagedResult<CategoryDto>>();
        Assert.NotNull(categories);
        Assert.True(categories.Items.Count > 0);

        var categoryId = categories.Items[0].Id;

        var response = await _client.GetAsync($"/api/v1/catalog/products?categoryId={categoryId}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<ProductDto>>();

        Assert.NotNull(result);
        Assert.All(result.Items, p => Assert.Equal(categoryId, p.CategoryId));
    }

    [Fact]
    public async Task GetProducts_SearchByName_ReturnsMatchingProducts()
    {
        var response = await _client.GetAsync("/api/v1/catalog/products?search=Headphones");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<ProductDto>>();

        Assert.NotNull(result);
        Assert.True(result.Items.Count > 0);
        Assert.Contains(result.Items, p => p.Name.Contains("Headphones", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetProductById_ExistingProduct_ReturnsProduct()
    {
        // Get a product from the list first
        var listResponse = await _client.GetAsync("/api/v1/catalog/products?pageSize=1");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<ProductDto>>();
        Assert.NotNull(list);
        Assert.True(list.Items.Count > 0);

        var id = list.Items[0].Id;
        var response = await _client.GetAsync($"/api/v1/catalog/products/{id}");

        response.EnsureSuccessStatusCode();
        var product = await response.Content.ReadFromJsonAsync<ProductDto>();
        Assert.NotNull(product);
        Assert.Equal(id, product.Id);
        Assert.False(string.IsNullOrWhiteSpace(product.Name));
    }

    [Fact]
    public async Task GetProductById_NonExistingProduct_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/catalog/products/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetProductCount_ReturnsPositiveCount()
    {
        var response = await _client.GetAsync("/api/v1/catalog/products/count");

        response.EnsureSuccessStatusCode();
        var count = await response.Content.ReadFromJsonAsync<int>();
        Assert.True(count > 0);
    }

    // ─── Categories: Read ────────────────────────────────────────────────

    [Fact]
    public async Task GetCategories_ReturnsSeededCategories()
    {
        var response = await _client.GetAsync("/api/v1/catalog/categories");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<CategoryDto>>();

        Assert.NotNull(result);
        Assert.True(result.Items.Count > 0);
    }

    [Fact]
    public async Task GetCategoryById_ExistingCategory_ReturnsCategory()
    {
        var listResponse = await _client.GetAsync("/api/v1/catalog/categories?pageSize=1");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<PagedResult<CategoryDto>>();
        Assert.NotNull(list);
        Assert.True(list.Items.Count > 0);

        var id = list.Items[0].Id;
        var response = await _client.GetAsync($"/api/v1/catalog/categories/{id}");

        response.EnsureSuccessStatusCode();
        var category = await response.Content.ReadFromJsonAsync<CategoryDto>();
        Assert.NotNull(category);
        Assert.Equal(id, category.Id);
        Assert.False(string.IsNullOrWhiteSpace(category.Name));
    }

    [Fact]
    public async Task GetCategoryById_NonExistingCategory_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/catalog/categories/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCategoryCount_ReturnsPositiveCount()
    {
        var response = await _client.GetAsync("/api/v1/catalog/categories/count");

        response.EnsureSuccessStatusCode();
        var count = await response.Content.ReadFromJsonAsync<int>();
        Assert.True(count > 0);
    }

    // ─── Products: Write (requires Admin JWT — expect 401 without) ───────

    [Fact]
    public async Task CreateProduct_WithoutAuth_ReturnsUnauthorized()
    {
        var request = new CreateProductRequest
        {
            Name = "Test Product",
            Description = "A test product",
            Price = 9.99m,
            ImageUrl = "/images/test.jpg",
            CategoryId = 1
        };

        var response = await _client.PostAsJsonAsync("/api/v1/catalog/products", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateProduct_WithoutAuth_ReturnsUnauthorized()
    {
        var request = new UpdateProductRequest
        {
            Name = "Updated",
            Description = "Updated",
            Price = 1.00m,
            ImageUrl = "/images/test.jpg",
            CategoryId = 1
        };

        var response = await _client.PutAsJsonAsync("/api/v1/catalog/products/1", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProduct_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _client.DeleteAsync("/api/v1/catalog/products/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── Categories: Write (requires Admin JWT — expect 401 without) ─────

    [Fact]
    public async Task CreateCategory_WithoutAuth_ReturnsUnauthorized()
    {
        var dto = new CategoryDto { Name = "Test", Description = "Test category" };

        var response = await _client.PostAsJsonAsync("/api/v1/catalog/categories", dto);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateCategory_WithoutAuth_ReturnsUnauthorized()
    {
        var dto = new CategoryDto { Name = "Updated", Description = "Updated" };

        var response = await _client.PutAsJsonAsync("/api/v1/catalog/categories/1", dto);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCategory_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _client.DeleteAsync("/api/v1/catalog/categories/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
