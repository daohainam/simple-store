using SimpleStore.Identity.API.Models;

namespace SimpleStore.Identity.API.Services;

public interface IRefreshTokenService
{
    Task<(string RawToken, DateTime ExpiresAt)> IssueAsync(string userId, CancellationToken cancellationToken = default);
    Task<RefreshToken?> ValidateAsync(string rawToken, CancellationToken cancellationToken = default);
    Task<(string RawToken, DateTime ExpiresAt)?> RotateAsync(string rawToken, CancellationToken cancellationToken = default);
    Task RevokeAsync(string rawToken, CancellationToken cancellationToken = default);
}
