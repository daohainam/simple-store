using SimpleStore.Identity.API.Models;

namespace SimpleStore.Identity.API.Services;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAt) GenerateAccessToken(ApplicationUser user, IList<string> roles);
}
