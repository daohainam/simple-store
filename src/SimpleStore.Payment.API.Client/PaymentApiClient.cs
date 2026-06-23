using System.Net;
using System.Net.Http.Json;

namespace SimpleStore.Payment.API.Client;

public class PaymentApiClient : IPaymentApiClient
{
    private readonly HttpClient _http;

    public PaymentApiClient(HttpClient http) => _http = http;

    public async Task<AccountDto> GetMyAccountAsync(CancellationToken cancellationToken = default) =>
        (await _http.GetFromJsonAsync<AccountDto>("api/v1/payment/account", cancellationToken))!;

    public async Task<AccountDto> DepositAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/v1/payment/account/deposit", new DepositRequest { Amount = amount }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<TransactionDto>> GetMyTransactionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<TransactionDto>>("api/v1/payment/account/transactions", cancellationToken);
        return result ?? new List<TransactionDto>();
    }

    public async Task<PagedResult<AccountDto>> GetAccountsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<PagedResult<AccountDto>>(
            $"api/v1/payment/admin/accounts?page={page}&pageSize={pageSize}", cancellationToken);
        return result ?? new PagedResult<AccountDto> { Page = page, PageSize = pageSize };
    }

    public async Task<int> GetAccountCountAsync(CancellationToken cancellationToken = default) =>
        await _http.GetFromJsonAsync<int>("api/v1/payment/admin/accounts/count", cancellationToken);

    public async Task<AccountDto?> GetAccountByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/v1/payment/admin/accounts/{userId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountDto>(cancellationToken);
    }

    public async Task<AccountDto> DepositForUserAsync(string userId, decimal amount, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/payment/admin/accounts/{userId}/deposit", new DepositRequest { Amount = amount }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountDto>(cancellationToken))!;
    }

    public async Task<IReadOnlyList<TransactionDto>> GetTransactionsForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<TransactionDto>>(
            $"api/v1/payment/admin/accounts/{userId}/transactions", cancellationToken);
        return result ?? new List<TransactionDto>();
    }
}
