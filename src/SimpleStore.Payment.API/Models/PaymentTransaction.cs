namespace SimpleStore.Payment.API.Models;

// Append-only ledger entry against a PaymentAccount. BalanceAfter snapshots the running balance
// so the transaction history renders without recomputation. OrderId / CorrelationId are populated
// only for Payment rows (the saga-driven debit); they are null for Deposit rows.
public class PaymentTransaction
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public PaymentAccount Account { get; set; } = null!;

    public PaymentTransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }

    public int? OrderId { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
