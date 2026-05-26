using System.Net;
using System.Net.Http.Json;

namespace SimpleStore.Identity.API.Client;

public class IdentityApiClient : IIdentityApiClient
{
    private readonly HttpClient _http;

    public IdentityApiClient(HttpClient http) => _http = http;

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/identity/login", request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
    }

    public async Task<LoginResponse?> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/identity/register", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
    }

    public async Task<LoginResponse?> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/identity/refresh", request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
    }

    public async Task LogoutAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/identity/logout", request, cancellationToken);
        // Idempotent: ignore 4xx so callers can blindly call on already-cleared sessions.
        if ((int)response.StatusCode >= 500) response.EnsureSuccessStatusCode();
    }

    public async Task<string> GetPasskeyAssertionOptionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("api/v1/identity/passkey/assertion-options", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<LoginResponse?> PasskeyAssertionAsync(PasskeyAssertionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/identity/passkey/assertion", request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
    }

    public async Task<UserInfo?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/identity/me", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken);
    }

    public async Task UpdateMeAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/v1/identity/me", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> GetPasskeyCreationOptionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync("api/v1/identity/passkey/creation-options", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task PasskeyAttestationAsync(PasskeyAttestationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/identity/passkey/attestation", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<UserPasskeyInfo>> GetPasskeysAsync(CancellationToken cancellationToken = default)
    {
        var list = await _http.GetFromJsonAsync<List<UserPasskeyInfo>>("api/v1/identity/passkeys", cancellationToken);
        return list ?? new List<UserPasskeyInfo>();
    }

    public async Task DeletePasskeyAsync(string credentialIdBase64, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"api/v1/identity/passkeys/{Uri.EscapeDataString(credentialIdBase64)}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<PagedResult<UserSummary>> GetUsersAsync(
        int page = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"api/v1/identity/users?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search)}";

        var result = await _http.GetFromJsonAsync<PagedResult<UserSummary>>(query, cancellationToken);
        return result ?? new PagedResult<UserSummary> { Page = page, PageSize = pageSize };
    }

    public async Task<int> GetUserCountAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<int>("api/v1/identity/users/count", cancellationToken);

    public async Task<UserInfo?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/identity/users/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken);
    }

    public async Task UpdateUserAsync(string id, UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/v1/identity/users/{Uri.EscapeDataString(id)}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task LockUserAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/v1/identity/users/{Uri.EscapeDataString(id)}/lock", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UnlockUserAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"api/v1/identity/users/{Uri.EscapeDataString(id)}/unlock", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
