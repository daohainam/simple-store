using SimpleStore.Identity.API.Client;

namespace SimpleStore.Admin.Services.Auth;

// Handler-free Identity client used *only* for refresh-token rotation.
//
// Unlike Web, Admin attaches BearerTokenHandler to IIdentityApiClient (the /users admin
// endpoints need a JWT). That makes the handler unable to depend on IIdentityApiClient itself:
// building the identity client's handler chain resolves BearerTokenHandler, which resolves the
// identity client, which re-enters the same IHttpClientFactory handler entry — the
// "ValueFactory attempted to access the Value property of this instance" failure.
//
// Refreshing through a client with no message handlers breaks that cycle, and also prevents a
// /refresh call from recursively triggering another refresh.
public sealed class TokenRefreshClient(HttpClient http)
{
    private readonly IIdentityApiClient _identity = new IdentityApiClient(http);

    public Task<LoginResponse?> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
        => _identity.RefreshAsync(request, cancellationToken);
}
