using System.Collections.Concurrent;
using SimpleStore.Identity.API.Client;

namespace SimpleStore.Admin.Services.Auth;

// v9: single-flight coordinator for refresh-token rotations. Mirrors Web/Services/Auth/TokenRefreshCoordinator.cs.
// See that file for design notes — Admin duplicates the type because Web and Admin have no
// shared application-services project (per CLAUDE.md conventions).
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
            _inFlight.TryRemove(key, out _);
        }
    }
}
