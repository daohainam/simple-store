using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Catalog.API.Client;
using SimpleStore.Catalog.API.Data;
using SimpleStore.Catalog.API.Models;
using SimpleStore.Contracts;

namespace SimpleStore.Catalog.API.Services;

public class CatalogService : ICatalogService
{
    private const int MaxPageSize = 100;

    private readonly CatalogDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;

    public CatalogService(CatalogDbContext context, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
    }

    // ---------- Products ----------

    public async Task<PagedResult<ProductDto>> GetProductsAsync(
        int page,
        int pageSize,
        int? categoryId,
        string? searchTerm,
        CancellationToken ct = default)
    {
        (page, pageSize) = ClampPaging(page, pageSize);

        var query = _context.Products.Include(p => p.Category).AsNoTracking().AsQueryable();
        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = query.Where(p => p.Name.Contains(searchTerm) || p.Description.Contains(searchTerm));

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => ToDto(p))
            .ToListAsync(ct);

        return new PagedResult<ProductDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<ProductDto?> GetProductByIdAsync(int id, CancellationToken ct = default)
    {
        var product = await _context.Products
            .Include(p => p.Category)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return product is null ? null : ToDto(product);
    }

    public Task<int> GetProductCountAsync(CancellationToken ct = default) =>
        _context.Products.CountAsync(ct);

    public async Task<ProductDto> CreateProductAsync(CreateProductRequest request, CancellationToken ct = default)
    {
        // Stock starts at 0 — Inventory.API is the source of truth. An admin establishes initial
        // stock by issuing a receipt note in Inventory, which flows back here as a
        // StockLevelChangedEvent and updates the cached Product.Stock.
        var product = new Product
        {
            Name = request.Name,
            Description = request.Description,
            Price = request.Price,
            Stock = 0,
            ImageUrl = request.ImageUrl,
            CategoryId = request.CategoryId
        };
        _context.Products.Add(product);
        await _context.SaveChangesAsync(ct);

        // Reload with Category for the response.
        await _context.Entry(product).Reference(p => p.Category).LoadAsync(ct);
        return ToDto(product);
    }

    public async Task<bool> UpdateProductAsync(int id, UpdateProductRequest request, CancellationToken ct = default)
    {
        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return false;

        // Stock is intentionally NOT updated here — it is owned by Inventory.API and refreshed via
        // StockLevelChangedEvent.
        product.Name = request.Name;
        product.Description = request.Description;
        product.Price = request.Price;
        product.ImageUrl = request.ImageUrl;
        product.CategoryId = request.CategoryId;

        // Persist + publish atomically: ProductUpdatedEvent is written to OutboxMessage in the same
        // transaction as the product update. Cart.API consumes it to refresh denormalized line items.
        // CategoryName is loaded separately because the consumer wants the same shape ProductDto has.
        await using var tx = await _context.Database.BeginTransactionAsync(ct);

        await _context.SaveChangesAsync(ct);

        await _context.Entry(product).Reference(p => p.Category).LoadAsync(ct);

        await _publishEndpoint.Publish(new ProductUpdatedEvent
        {
            ProductId = product.Id,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            ImageUrl = product.ImageUrl,
            Stock = product.Stock,
            CategoryId = product.CategoryId,
            CategoryName = product.Category?.Name ?? string.Empty
        }, ct);

        await _context.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return true;
    }

    public async Task<bool> DeleteProductAsync(int id, CancellationToken ct = default)
    {
        var product = await _context.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return false;
        _context.Products.Remove(product);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Categories ----------

    public async Task<PagedResult<CategoryDto>> GetCategoriesAsync(int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = ClampPaging(page, pageSize);

        var query = _context.Categories.AsNoTracking().AsQueryable();

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                ProductCount = c.Products.Count()
            })
            .ToListAsync(ct);

        return new PagedResult<CategoryDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<CategoryDto?> GetCategoryByIdAsync(int id, CancellationToken ct = default)
    {
        var category = await _context.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                ProductCount = c.Products.Count()
            })
            .FirstOrDefaultAsync(ct);
        return category;
    }

    public Task<int> GetCategoryCountAsync(CancellationToken ct = default) =>
        _context.Categories.CountAsync(ct);

    public async Task<CategoryDto> CreateCategoryAsync(CategoryDto dto, CancellationToken ct = default)
    {
        var category = new Category
        {
            Name = dto.Name,
            Description = dto.Description
        };
        _context.Categories.Add(category);
        await _context.SaveChangesAsync(ct);
        return new CategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Description = category.Description,
            ProductCount = 0
        };
    }

    public async Task<bool> UpdateCategoryAsync(int id, CategoryDto dto, CancellationToken ct = default)
    {
        var category = await _context.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null) return false;

        category.Name = dto.Name;
        category.Description = dto.Description;

        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DeleteCategoryResult> DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        var category = await _context.Categories
            .Include(c => c.Products)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (category is null) return DeleteCategoryResult.NotFound;
        if (category.Products.Count > 0) return DeleteCategoryResult.HasProducts;

        _context.Categories.Remove(category);
        await _context.SaveChangesAsync(ct);
        return DeleteCategoryResult.Deleted;
    }

    // ---------- Helpers ----------

    private static (int page, int pageSize) ClampPaging(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;
        return (page, pageSize);
    }

    private static ProductDto ToDto(Product p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Price = p.Price,
        ImageUrl = p.ImageUrl,
        Stock = p.Stock,
        CategoryId = p.CategoryId,
        CategoryName = p.Category?.Name ?? string.Empty
    };
}
