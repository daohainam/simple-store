using Microsoft.AspNetCore.Mvc;
using SimpleStore.Cart.API.Client;
using SimpleStore.Catalog.API.Client;
using SimpleStore.Web.Services.Cart;

namespace SimpleStore.Web.Controllers;

public class CartController : Controller
{
    private readonly ICartApiClient _cart;
    private readonly ICatalogApiClient _catalog;
    private readonly CartCookieManager _cookies;

    public CartController(ICartApiClient cart, ICatalogApiClient catalog, CartCookieManager cookies)
    {
        _cart = cart;
        _catalog = catalog;
        _cookies = cookies;
    }

    public async Task<IActionResult> Index()
    {
        var dto = await _cart.GetAsync();
        return View(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Add(int productId, int quantity = 1)
    {
        // Anonymous shoppers need a cart-id cookie before we can persist anything in Cart.API.
        if (User.Identity?.IsAuthenticated != true)
        {
            _cookies.EnsureCartId();
        }

        var product = await _catalog.GetProductByIdAsync(productId);
        if (product is null)
        {
            TempData["Error"] = "Product not found.";
            return RedirectToAction("Index", "Catalog");
        }

        await _cart.AddItemAsync(new AddCartItemRequest
        {
            ProductId = product.Id,
            ProductName = product.Name,
            UnitPrice = product.Price,
            ImageUrl = product.ImageUrl,
            Quantity = quantity
        });

        TempData["Success"] = "Item added to cart!";
        return RedirectToAction("Index", "Catalog");
    }

    [HttpPost]
    public async Task<IActionResult> Update(int productId, int quantity)
    {
        await _cart.UpdateItemAsync(productId, quantity);
        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Remove(int productId)
    {
        await _cart.RemoveItemAsync(productId);
        return RedirectToAction("Index");
    }
}
