using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Payment.API.Client;
using SimpleStore.Payment.API.Data;
using SimpleStore.Payment.API.Models;
using SimpleStore.Payment.API.Observability;

namespace SimpleStore.Payment.API.Services;

public class PaymentService : IPaymentService
{
    private const int MaxPageSize = 100;

    private readonly PaymentDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly TimeProvider _clock;
    private readonly ILogger<PaymentService> _log;

    public PaymentService(
        PaymentDbContext context,
        IPublishEndpoint publishEndpoint,
        TimeProvider clock,
        ILogger<PaymentService> log)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
        _clock = clock;
        _log = log;
    }

    public async Task<AccountDto> GetOrCreateAccountAsync(string userId, CancellationToken ct = default)
    {
        var existing = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (existing is not null) return ToDto(existing);

        // Provision a fresh zero-balance account. Re-check inside the transaction so two concurrent
        // first-access requests don't both insert (the unique index on UserId would reject the loser).
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            var account = await _context.Accounts.FirstOrDefaultAsync(a => a.UserId == userId, ct);
            if (account is null)
            {
                account = NewAccount(userId);
                _context.Accounts.Add(account);
                await _context.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return ToDto(account);
        });
    }

    public async Task<AccountDto> DepositAsync(string userId, decimal amount, CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Deposit amount must be positive.");

        var strategy = _context.Database.CreateExecutionStrategy();
        var dto = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            var account = await GetOrAddTrackedAsync(userId, ct);

            var now = _clock.GetUtcNow().UtcDateTime;
            account.Balance += amount;
            account.UpdatedAt = now;
            _context.Transactions.Add(new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Type = PaymentTransactionType.Deposit,
                Amount = amount,
                BalanceAfter = account.Balance,
                Description = "Deposit",
                CreatedAt = now
            });

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToDto(account);
        });

        Telemetry.Deposits.Add(1);
        _log.LogInformation("Deposited {Amount} for user {UserId}; new balance {Balance}.", amount, userId, dto.Balance);
        return dto;
    }

    public async Task<IReadOnlyList<TransactionDto>> GetTransactionsAsync(string userId, CancellationToken ct = default)
    {
        return await _context.Transactions
            .AsNoTracking()
            .Where(t => t.Account.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => ToDto(t))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<AccountDto>> GetAccountsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        (page, pageSize) = ClampPaging(page, pageSize);

        var query = _context.Accounts.AsNoTracking();
        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.Balance)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => ToDto(a))
            .ToListAsync(ct);

        return new PagedResult<AccountDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public Task<int> GetAccountCountAsync(CancellationToken ct = default) =>
        _context.Accounts.CountAsync(ct);

    public async Task<AccountDto?> GetAccountByUserAsync(string userId, CancellationToken ct = default)
    {
        var account = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, ct);
        return account is null ? null : ToDto(account);
    }

    public async Task DebitForOrderAsync(string userId, int orderId, Guid correlationId, decimal amount, CancellationToken ct = default)
    {
        // Open a logging scope keyed by CorrelationId so this debit joins the saga's audit trail
        // across Order / Checkout / Inventory / Payment in the Aspire dashboard.
        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["OrderId"] = orderId
        });

        // The publish rides the same transaction's bus outbox flush as the balance change, so the
        // reply and the debit commit atomically — identical pattern to OrderService.CreateOrderAsync.
        var strategy = _context.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            var account = await GetOrAddTrackedAsync(userId, ct);
            var now = _clock.GetUtcNow();

            if (account.Balance >= amount)
            {
                account.Balance -= amount;
                account.UpdatedAt = now.UtcDateTime;
                var txn = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    Type = PaymentTransactionType.Payment,
                    Amount = amount,
                    BalanceAfter = account.Balance,
                    OrderId = orderId,
                    CorrelationId = correlationId,
                    Description = $"Payment for order #{orderId}",
                    CreatedAt = now.UtcDateTime
                };
                _context.Transactions.Add(txn);

                await _publishEndpoint.Publish(new PaymentSucceededEventV1
                {
                    CorrelationId = correlationId,
                    OrderId = orderId,
                    TransactionId = txn.Id,
                    Amount = amount,
                    PaidAt = now
                }, ct);

                await _context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            }

            await _publishEndpoint.Publish(new PaymentFailedEventV1
            {
                CorrelationId = correlationId,
                OrderId = orderId,
                Reason = PaymentFailureReason.InsufficientFunds,
                Amount = amount,
                FailedAt = now
            }, ct);

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return false;
        });

        if (succeeded)
        {
            Telemetry.PaymentsSucceeded.Add(1);
            _log.LogInformation("Payment of {Amount} for order {OrderId} succeeded.", amount, orderId);
        }
        else
        {
            Telemetry.PaymentsFailed.Add(1,
                new KeyValuePair<string, object?>("reason", PaymentFailureReason.InsufficientFunds));
            _log.LogInformation(
                "Payment of {Amount} for order {OrderId} rejected — insufficient funds.", amount, orderId);
        }
    }

    // Returns a TRACKED account (existing or newly-added-but-unsaved). Caller saves + commits.
    private async Task<PaymentAccount> GetOrAddTrackedAsync(string userId, CancellationToken ct)
    {
        var account = await _context.Accounts.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (account is null)
        {
            account = NewAccount(userId);
            _context.Accounts.Add(account);
        }
        return account;
    }

    private PaymentAccount NewAccount(string userId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return new PaymentAccount
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Balance = 0m,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static (int page, int pageSize) ClampPaging(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;
        return (page, pageSize);
    }

    private static AccountDto ToDto(PaymentAccount a) => new()
    {
        Id = a.Id,
        UserId = a.UserId,
        Balance = a.Balance,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt
    };

    private static TransactionDto ToDto(PaymentTransaction t) => new()
    {
        Id = t.Id,
        Type = t.Type.ToString(),
        Amount = t.Amount,
        BalanceAfter = t.BalanceAfter,
        OrderId = t.OrderId,
        Description = t.Description,
        CreatedAt = t.CreatedAt
    };
}
