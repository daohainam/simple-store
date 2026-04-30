using Microsoft.AspNetCore.Mvc;
using SimpleStore.Web.Services;

namespace SimpleStore.Web.Controllers;

public class CartController : Controller
{
    private readonly ICartService _cart;
    public CartController(ICartService cart) => _cart = cart;

    public async Task<IActionResult> Index()
    {
        var items = await _cart.GetCartItemsAsync();
        return View(items);
    }

    [HttpPost]
    public async Task<IActionResult> Add(int productId, int quantity = 1)
    {
        await _cart.AddToCartAsync(productId, quantity);
        TempData["Success"] = "Item added to cart!";
        return RedirectToAction("Index", "Catalog");
    }

    [HttpPost]
    public async Task<IActionResult> Update(int productId, int quantity)
    {
        await _cart.UpdateQuantityAsync(productId, quantity);
        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Remove(int productId)
    {
        await _cart.RemoveFromCartAsync(productId);
        return RedirectToAction("Index");
    }
}
