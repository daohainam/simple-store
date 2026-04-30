using Microsoft.AspNetCore.Mvc;
using SimpleStore.Web.Services;

namespace SimpleStore.Web.ViewComponents;

public class CartCountViewComponent : ViewComponent
{
    private readonly ICartService _cart;
    public CartCountViewComponent(ICartService cart) => _cart = cart;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var count = await _cart.GetCartCountAsync();
        return View(count);
    }
}
