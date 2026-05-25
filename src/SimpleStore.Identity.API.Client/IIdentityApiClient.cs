namespace SimpleStore.Identity.API.Client;

public interface IIdentityApiClient
{
    // Anonymous
    Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<LoginResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<LoginResponse?> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);
    Task LogoutAsync(RefreshRequest request, CancellationToken cancellationToken = default);

    // Passkey (anonymous + authenticated mixed)
    Task<string> GetPasskeyAssertionOptionsAsync(CancellationToken cancellationToken = default);
    Task<LoginResponse?> PasskeyAssertionAsync(PasskeyAssertionRequest request, CancellationToken cancellationToken = default);

    // Authenticated (bearer)
    Task<UserInfo?> GetMeAsync(CancellationToken cancellationToken = default);
    Task UpdateMeAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default);

    Task<string> GetPasskeyCreationOptionsAsync(CancellationToken cancellationToken = default);
    Task PasskeyAttestationAsync(PasskeyAttestationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserPasskeyInfo>> GetPasskeysAsync(CancellationToken cancellationToken = default);
    Task DeletePasskeyAsync(string credentialIdBase64, CancellationToken cancellationToken = default);

    // Admin
    Task<PagedResult<UserSummary>> GetUsersAsync(int page = 1, int pageSize = 20, string? search = null, CancellationToken cancellationToken = default);
    Task<int> GetUserCountAsync(CancellationToken cancellationToken = default);
    Task<UserInfo?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default);
    Task UpdateUserAsync(string id, UpdateProfileRequest request, CancellationToken cancellationToken = default);
    Task LockUserAsync(string id, CancellationToken cancellationToken = default);
    Task UnlockUserAsync(string id, CancellationToken cancellationToken = default);
}
