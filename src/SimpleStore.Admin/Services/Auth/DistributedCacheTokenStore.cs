using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace SimpleStore.Admin.Services.Auth;

// Browser holds only an opaque session id (HttpOnly+Secure cookie); JWT + refresh token
// live server-side in IDistributedCache. Identical pattern to SimpleStore.Web.
public class DistributedCacheTokenStore
{
    public const string SessionCookieName = "ss_session";
    private const string CacheKeyPrefix = "auth:";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDistributedCache _cache;

    public DistributedCacheTokenStore(IHttpContextAccessor httpContextAccessor, IDistributedCache cache)
    {
        _httpContextAccessor = httpContextAccessor;
        _cache = cache;
    }

    public async Task<TokenSet?> GetAsync(CancellationToken cancellationToken = default)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return null;
        if (!ctx.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId) || string.IsNullOrEmpty(sessionId))
            return null;

        var json = await _cache.GetStringAsync(CacheKeyPrefix + sessionId, cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<TokenSet>(json);
    }

    public async Task SetAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        var ctx = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext required to set tokens.");

        if (!ctx.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId) || string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append(SessionCookieName, sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/"
            });
        }

        var json = JsonSerializer.Serialize(tokens);
        await _cache.SetStringAsync(CacheKeyPrefix + sessionId, json, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromDays(30)
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null) return;
        if (ctx.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId) && !string.IsNullOrEmpty(sessionId))
        {
            await _cache.RemoveAsync(CacheKeyPrefix + sessionId, cancellationToken);
        }
        ctx.Response.Cookies.Delete(SessionCookieName);
    }
}
