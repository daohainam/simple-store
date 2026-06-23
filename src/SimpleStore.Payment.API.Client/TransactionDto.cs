namespace SimpleStore.Payment.API.Client;

public class TransactionDto
{
    public Guid Id { get; set; }
    /// <summary>"Deposit" or "Payment".</summary>
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public int? OrderId { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}
