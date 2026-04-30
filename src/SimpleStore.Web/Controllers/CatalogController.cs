using Microsoft.AspNetCore.Mvc;
using SimpleStore.Web.Services;

namespace SimpleStore.Web.Controllers;

public class CatalogController : Controller
{
    private readonly ICatalogService _catalog;
    public CatalogController(ICatalogService catalog) => _catalog = catalog;

    public async Task<IActionResult> Index(int? categoryId, string? search)
    {
        ViewBag.Categories = await _catalog.GetCategoriesAsync();
        ViewBag.SelectedCategoryId = categoryId;
        ViewBag.SearchTerm = search;
        var products = await _catalog.GetProductsAsync(categoryId, search);
        return View(products);
    }

    public async Task<IActionResult> Details(int id)
    {
        var product = await _catalog.GetProductByIdAsync(id);
        if (product == null) return NotFound();
        return View(product);
    }
}
