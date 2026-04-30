using SimpleStore.Data.Models;
namespace SimpleStore.Web.Services;

public interface ICatalogService
{
    Task<IEnumerable<Product>> GetProductsAsync(int? categoryId = null, string? searchTerm = null);
    Task<Product?> GetProductByIdAsync(int id);
    Task<IEnumerable<Category>> GetCategoriesAsync();
}
