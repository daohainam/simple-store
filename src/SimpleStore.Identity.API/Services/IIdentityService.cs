using SimpleStore.Identity.API.Client;

namespace SimpleStore.Identity.API.Services;

public interface IIdentityService
{
    Task<LoginResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<LoginResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task<LoginResponse?> IssueTokensAsync(string userId, CancellationToken cancellationToken = default);
    Task<UserInfo?> GetUserInfoAsync(string userId, CancellationToken cancellationToken = default);
}
