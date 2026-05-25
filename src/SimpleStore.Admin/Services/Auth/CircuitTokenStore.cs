namespace SimpleStore.Admin.Services.Auth;

// Blazor Server caveat: IHttpContextAccessor.HttpContext is only reliable during the initial
// HTTP request that establishes the SignalR circuit. We capture the token on first read (while
// HttpContext is still live) and serve it from the scoped circuit state thereafter.
public class CircuitTokenStore : ITokenStore
{
    private readonly DistributedCacheTokenStore _inner;
    private TokenSet? _cached;
    private bool _hasCached;

    public CircuitTokenStore(DistributedCacheTokenStore inner) => _inner = inner;

    public async Task<TokenSet?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var live = await _inner.GetAsync(cancellationToken);
            if (live is not null)
            {
                _cached = live;
                _hasCached = true;
                return live;
            }
        }
        catch
        {
            // HttpContext may be unavailable in interactive Blazor — fall back to cached.
        }

        return _hasCached ? _cached : null;
    }

    public async Task SetAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        _cached = tokens;
        _hasCached = true;
        await _inner.SetAsync(tokens, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _cached = null;
        _hasCached = false;
        await _inner.ClearAsync(cancellationToken);
    }
}
