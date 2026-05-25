using Microsoft.EntityFrameworkCore;
using SimpleStore.Data;
using SimpleStore.Data.Models;

namespace SimpleStore.Web.Services;

public class CatalogService : ICatalogService
{
    private readonly CatalogDbContext _context;
    public CatalogService(CatalogDbContext context) => _context = context;

    public async Task<IEnumerable<Product>> GetProductsAsync(int? categoryId = null, string? searchTerm = null)
    {
        var query = _context.Products.Include(p => p.Category).AsQueryable();
        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = query.Where(p => p.Name.Contains(searchTerm) || p.Description.Contains(searchTerm));
        return await query.ToListAsync();
    }

    public async Task<Product?> GetProductByIdAsync(int id) =>
        await _context.Products.Include(p => p.Category).FirstOrDefaultAsync(p => p.Id == id);

    public async Task<IEnumerable<Category>> GetCategoriesAsync() =>
        await _context.Categories.ToListAsync();
}
