namespace SimpleStore.Web.Services.Cart;

// Stamps X-Cart-Id on outbound Cart.API calls when the user is anonymous.
// Cart.API prefers the JWT "sub" claim when both are present, so this header is harmless for auth users.
public class CartIdHandler : DelegatingHandler
{
    private const string HeaderName = "X-Cart-Id";

    private readonly CartCookieManager _cookies;

    public CartIdHandler(CartCookieManager cookies) => _cookies = cookies;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cartId = _cookies.TryGetCartId();
        if (!string.IsNullOrEmpty(cartId) && !request.Headers.Contains(HeaderName))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, cartId);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
