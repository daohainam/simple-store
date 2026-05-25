using Microsoft.AspNetCore.Identity;
using SimpleStore.Identity.API.Client;
using SimpleStore.Identity.API.Models;

namespace SimpleStore.Identity.API.Services;

public class IdentityService : IIdentityService
{
    public const string CustomerRole = "Customer";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtTokenService _jwt;
    private readonly IRefreshTokenService _refresh;

    public IdentityService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtTokenService jwt,
        IRefreshTokenService refresh)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _jwt = jwt;
        _refresh = refresh;
    }

    public async Task<LoginResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return null;
        if (await _userManager.FindByEmailAsync(request.Email) is not null) return null;

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true,
            FullName = request.FullName
        };

        var create = await _userManager.CreateAsync(user, request.Password);
        if (!create.Succeeded) return null;

        await _userManager.AddToRoleAsync(user, CustomerRole);

        return await IssueTokensAsync(user.Id, cancellationToken);
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null) return null;

        var check = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
        if (!check.Succeeded) return null;

        return await IssueTokensAsync(user.Id, cancellationToken);
    }

    public async Task<LoginResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var existing = await _refresh.ValidateAsync(refreshToken, cancellationToken);
        if (existing is null) return null;

        var rotated = await _refresh.RotateAsync(refreshToken, cancellationToken);
        if (rotated is null) return null;

        var user = await _userManager.FindByIdAsync(existing.UserId);
        if (user is null) return null;

        var roles = await _userManager.GetRolesAsync(user);
        var (access, expiresAt) = _jwt.GenerateAccessToken(user, roles);

        return new LoginResponse
        {
            AccessToken = access,
            RefreshToken = rotated.Value.RawToken,
            ExpiresAt = expiresAt,
            User = ToUserInfo(user, roles)
        };
    }

    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        _refresh.RevokeAsync(refreshToken, cancellationToken);

    public async Task<LoginResponse?> IssueTokensAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return null;

        var roles = await _userManager.GetRolesAsync(user);
        var (access, expiresAt) = _jwt.GenerateAccessToken(user, roles);
        var (refresh, _) = await _refresh.IssueAsync(user.Id, cancellationToken);

        return new LoginResponse
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = expiresAt,
            User = ToUserInfo(user, roles)
        };
    }

    public async Task<UserInfo?> GetUserInfoAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return null;
        var roles = await _userManager.GetRolesAsync(user);
        return ToUserInfo(user, roles);
    }

    internal static UserInfo ToUserInfo(ApplicationUser user, IList<string> roles) => new()
    {
        Id = user.Id,
        Email = user.Email ?? string.Empty,
        FullName = user.FullName,
        Roles = roles.ToArray()
    };
}
