using SimpleStore.Identity.API.Client;

namespace SimpleStore.Admin.Services.Auth;

// Outbound DelegatingHandler. Same pattern as Web — see Web/Services/Auth/BearerTokenHandler.cs.
public class BearerTokenHandler : DelegatingHandler
{
    private readonly ITokenStore _tokens;
    private readonly IIdentityApiClient _identity;
    private readonly ILogger<BearerTokenHandler> _logger;

    public BearerTokenHandler(ITokenStore tokens, IIdentityApiClient identity, ILogger<BearerTokenHandler> logger)
    {
        _tokens = tokens;
        _identity = identity;
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

        if (current.ExpiresAt > DateTime.UtcNow.AddSeconds(30)) return current.AccessToken;
        if (string.IsNullOrEmpty(current.RefreshToken)) return current.AccessToken;

        try
        {
            var rotated = await _identity.RefreshAsync(new RefreshRequest { RefreshToken = current.RefreshToken }, cancellationToken);
            if (rotated is null) return null;
            var next = new TokenSet
            {
                AccessToken = rotated.AccessToken,
                RefreshToken = rotated.RefreshToken,
                ExpiresAt = rotated.ExpiresAt
            };
            await _tokens.SetAsync(next, cancellationToken);
            return next.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh token rotation failed; outbound call will be unauthenticated.");
            return null;
        }
    }
}
