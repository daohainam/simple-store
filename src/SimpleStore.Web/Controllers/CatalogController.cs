using Microsoft.AspNetCore.Mvc;
using SimpleStore.Catalog.API.Client;

namespace SimpleStore.Web.Controllers;

public class CatalogController : Controller
{
    // Storefront grid renders 12 products per page.
    private const int PageSize = 12;
    // The category sidebar isn't paged in the UI; pull a large first page.
    private const int CategorySidebarSize = 100;

    private readonly ICatalogApiClient _catalog;
    public CatalogController(ICatalogApiClient catalog) => _catalog = catalog;

    public async Task<IActionResult> Index(int? categoryId, string? search, int page = 1)
    {
        var categories = await _catalog.GetCategoriesAsync(page: 1, pageSize: CategorySidebarSize);
        ViewBag.Categories = categories.Items;
        ViewBag.SelectedCategoryId = categoryId;
        ViewBag.SearchTerm = search;

        var products = await _catalog.GetProductsAsync(page: page, pageSize: PageSize, categoryId: categoryId, search: search);
        return View(products);
    }

    public async Task<IActionResult> Details(int id)
    {
        var product = await _catalog.GetProductByIdAsync(id);
        if (product == null) return NotFound();
        return View(product);
    }
}
