namespace SimpleStore.Payment.API.Client;

public interface IPaymentApiClient
{
    // Storefront (current user) — owner enforced by the sub claim server-side. The account is
    // auto-provisioned at zero balance on first access, so these never 404 for a valid user.
    Task<AccountDto> GetMyAccountAsync(CancellationToken cancellationToken = default);
    Task<AccountDto> DepositAsync(decimal amount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransactionDto>> GetMyTransactionsAsync(CancellationToken cancellationToken = default);

    // Admin — gated by the "Admin" policy on the server.
    Task<PagedResult<AccountDto>> GetAccountsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<int> GetAccountCountAsync(CancellationToken cancellationToken = default);
    Task<AccountDto?> GetAccountByUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<AccountDto> DepositForUserAsync(string userId, decimal amount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransactionDto>> GetTransactionsForUserAsync(string userId, CancellationToken cancellationToken = default);
}
