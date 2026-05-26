using SimpleStore.Cart.API.Client;

namespace SimpleStore.Web.Services.Cart;

// Folds an anonymous cart into the authenticated user's cart on the first authenticated request
// after login. Doing it here (rather than inside the /Account/Login POST) sidesteps the fact that
// the ss_session cookie set during that POST isn't visible to outbound calls until the next request.
public class CartMergeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CartMergeMiddleware> _logger;

    public CartMergeMiddleware(RequestDelegate next, ILogger<CartMergeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICartApiClient cart, CartCookieManager cookies)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var anonCartId = cookies.TryGetCartId();
            if (!string.IsNullOrEmpty(anonCartId))
            {
                try
                {
                    await cart.MergeAsync(anonCartId, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Anonymous cart merge failed; clearing cookie regardless.");
                }
                cookies.Clear();
            }
        }
        await _next(context);
    }
}
