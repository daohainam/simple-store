using Microsoft.AspNetCore.Mvc;
using SimpleStore.Cart.API.Client;

namespace SimpleStore.Web.ViewComponents;

public class CartCountViewComponent : ViewComponent
{
    private readonly ICartApiClient _cart;
    public CartCountViewComponent(ICartApiClient cart) => _cart = cart;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var count = await _cart.GetCountAsync();
        return View(count);
    }
}
