namespace SimpleStore.Web.Services.Auth;

public interface ITokenStore
{
    Task<TokenSet?> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(TokenSet tokens, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
