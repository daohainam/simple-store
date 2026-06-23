using SimpleStore.Payment.API.Client;

namespace SimpleStore.Payment.API.Services;

public interface IPaymentService
{
    // Storefront (current user). The account is auto-provisioned at zero balance on first access.
    Task<AccountDto> GetOrCreateAccountAsync(string userId, CancellationToken ct = default);
    Task<AccountDto> DepositAsync(string userId, decimal amount, CancellationToken ct = default);
    Task<IReadOnlyList<TransactionDto>> GetTransactionsAsync(string userId, CancellationToken ct = default);

    // Admin
    Task<PagedResult<AccountDto>> GetAccountsAsync(int page, int pageSize, CancellationToken ct = default);
    Task<int> GetAccountCountAsync(CancellationToken ct = default);
    Task<AccountDto?> GetAccountByUserAsync(string userId, CancellationToken ct = default);

    // Saga-driven: charge the account for an order. Publishes PaymentSucceededEventV1 (sufficient
    // balance, debited) or PaymentFailedEventV1 (insufficient) inside the same transaction.
    Task DebitForOrderAsync(string userId, int orderId, Guid correlationId, decimal amount, CancellationToken ct = default);
}
