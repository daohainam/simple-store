using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimpleStore.Cart.API.Client;
using SimpleStore.Order.API.Client;
using SimpleStore.Web.ViewModels;

namespace SimpleStore.Web.Controllers;

[Authorize]
public class OrdersController : Controller
{
    private readonly IOrderApiClient _orders;
    private readonly ICartApiClient _cart;

    public OrdersController(IOrderApiClient orders, ICartApiClient cart)
    {
        _orders = orders;
        _cart = cart;
    }

    public async Task<IActionResult> Index()
    {
        var orders = await _orders.GetMyOrdersAsync();
        return View(orders);
    }

    public async Task<IActionResult> Details(int id)
    {
        var order = await _orders.GetMyOrderByIdAsync(id);
        if (order == null) return NotFound();
        return View(order);
    }

    public async Task<IActionResult> Checkout()
    {
        var cart = await _cart.GetAsync();
        if (cart.Items.Count == 0) return RedirectToAction("Index", "Cart");

        var model = new CheckoutViewModel
        {
            CartItems = cart.Items.ToList(),
            Total = cart.Total,
            FullName = User.FindFirstValue("name") ?? string.Empty,
            Email = User.FindFirstValue("email") ?? string.Empty
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(CheckoutViewModel model)
    {
        var cart = await _cart.GetAsync();

        if (!ModelState.IsValid)
        {
            model.CartItems = cart.Items.ToList();
            model.Total = cart.Total;
            return View(model);
        }

        if (cart.Items.Count == 0) return RedirectToAction("Index", "Cart");

        var request = new CreateOrderRequest
        {
            ShippingAddress = model.ShippingAddress,
            Items = cart.Items.Select(i => new OrderItemDto
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        var order = await _orders.CreateOrderAsync(request);
        await _cart.ClearAsync();
        return RedirectToAction("Confirmation", new { id = order.Id });
    }

    public async Task<IActionResult> Confirmation(int id)
    {
        var order = await _orders.GetMyOrderByIdAsync(id);
        if (order == null) return NotFound();
        return View(order);
    }
}
