using System.Collections.Concurrent;
using SimpleStore.Identity.API.Client;

namespace SimpleStore.Web.Services.Auth;

// v9: single-flight coordinator for refresh-token rotations. Without this, N concurrent requests
// from the same browser session that all see an expired access token would each call
// IIdentityApiClient.RefreshAsync — a thundering herd on Identity.API and a guaranteed loss
// because Identity invalidates the old refresh token on first use (rotate-on-use, v3+).
//
// The coordinator keys an in-flight Lazy<Task<...>> by the refresh-token value: the first caller
// installs the Lazy and triggers the network call; subsequent callers awaiting the same key
// re-use the same Task and get the same rotated tokens back. Once the task completes, the entry
// is removed so a future expiry (with the new refresh token) starts a fresh coordination.
//
// Memory growth is bounded by the count of concurrently-refreshing sessions, which in practice
// is tiny (one entry exists only while a refresh is in flight).
public sealed class TokenRefreshCoordinator
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LoginResponse?>>> _inFlight = new(StringComparer.Ordinal);

    public Task<LoginResponse?> RefreshAsync(string refreshToken, Func<Task<LoginResponse?>> refreshFn)
    {
        var lazy = _inFlight.GetOrAdd(refreshToken,
            _ => new Lazy<Task<LoginResponse?>>(refreshFn, LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndCleanupAsync(refreshToken, lazy);
    }

    private async Task<LoginResponse?> AwaitAndCleanupAsync(string key, Lazy<Task<LoginResponse?>> lazy)
    {
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            // Best-effort cleanup. Late callers that already grabbed this Lazy still receive its
            // cached result via lazy.Value; removing the dictionary entry only affects future callers
            // who would otherwise see a stale, already-completed task and reuse its (no-longer-valid)
            // refresh token.
            _inFlight.TryRemove(key, out _);
        }
    }
}
