namespace SimpleStore.Web.Services.Cart;

// Issues and tracks the opaque ss_cart cookie used to identify anonymous carts.
// The browser only ever holds this GUID — the actual cart lives in Cart.API (Redis).
public class CartCookieManager
{
    public const string CookieName = "ss_cart";
    private const string ItemsKey = "ss_cart_id";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public CartCookieManager(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public string EnsureCartId()
    {
        var ctx = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext required to set cart id.");

        if (ctx.Items.TryGetValue(ItemsKey, out var cached) && cached is string s && !string.IsNullOrEmpty(s))
            return s;

        if (ctx.Request.Cookies.TryGetValue(CookieName, out var existing) && !string.IsNullOrEmpty(existing))
        {
            ctx.Items[ItemsKey] = existing;
            return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append(CookieName, id, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
        ctx.Items[ItemsKey] = id;
        return id;
    }

    public string? TryGetCartId()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return null;

        if (ctx.Items.TryGetValue(ItemsKey, out var cached) && cached is string s && !string.IsNullOrEmpty(s))
            return s;

        if (ctx.Request.Cookies.TryGetValue(CookieName, out var existing) && !string.IsNullOrEmpty(existing))
            return existing;

        return null;
    }

    public void Clear()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return;
        ctx.Items.Remove(ItemsKey);
        ctx.Response.Cookies.Delete(CookieName);
    }
}
