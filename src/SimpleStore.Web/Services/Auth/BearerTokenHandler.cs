using SimpleStore.Identity.API.Client;

namespace SimpleStore.Web.Services.Auth;

// Outbound DelegatingHandler: stamps Authorization: Bearer on cross-service calls so the
// callee can validate the JWT issued by Identity.API. Auto-refreshes when access token expires.
//
// v9: refresh calls are coalesced via TokenRefreshCoordinator so N concurrent requests with the
// same expired access token make at most one network call to Identity.API. Without this,
// rotate-on-use refresh tokens would fail for all-but-one of the concurrent callers.
public class BearerTokenHandler : DelegatingHandler
{
    private readonly ITokenStore _tokens;
    private readonly IIdentityApiClient _identity;
    private readonly TokenRefreshCoordinator _coordinator;
    private readonly ILogger<BearerTokenHandler> _logger;

    public BearerTokenHandler(
        ITokenStore tokens,
        IIdentityApiClient identity,
        TokenRefreshCoordinator coordinator,
        ILogger<BearerTokenHandler> logger)
    {
        _tokens = tokens;
        _identity = identity;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetUsableAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string?> GetUsableAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = await _tokens.GetAsync(cancellationToken);
        if (current is null) return null;

        // 30s grace so we refresh slightly before expiry, matching JwtBearer ClockSkew.
        if (current.ExpiresAt > DateTime.UtcNow.AddSeconds(30)) return current.AccessToken;

        if (string.IsNullOrEmpty(current.RefreshToken)) return current.AccessToken;

        try
        {
            // v9: coordinator keys by the current refresh token so concurrent expired-token callers
            // share a single in-flight rotation. Note: we deliberately do NOT thread cancellationToken
            // into the inner refresh call — cancelling one of the racing callers must not abort the
            // shared rotation that the others are awaiting.
            var rotated = await _coordinator.RefreshAsync(
                current.RefreshToken,
                () => _identity.RefreshAsync(new RefreshRequest { RefreshToken = current.RefreshToken }, CancellationToken.None));
            if (rotated is null) return null;

            // Re-read the store: another concurrent caller may have already persisted the rotated
            // tokens. If we see an unchanged refresh token, we are the one responsible for writing.
            var latest = await _tokens.GetAsync(cancellationToken);
            if (latest is null || string.Equals(latest.RefreshToken, current.RefreshToken, StringComparison.Ordinal))
            {
                await _tokens.SetAsync(new TokenSet
                {
                    AccessToken = rotated.AccessToken,
                    RefreshToken = rotated.RefreshToken,
                    ExpiresAt = rotated.ExpiresAt
                }, cancellationToken);
                return rotated.AccessToken;
            }
            return latest.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh token rotation failed; outbound call will be unauthenticated.");
            return null;
        }
    }
}
