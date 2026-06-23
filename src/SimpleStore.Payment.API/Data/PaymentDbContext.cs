using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Payment.API.Models;

namespace SimpleStore.Payment.API.Data;

// Owns paymentdb. Holds payment accounts + the transaction ledger, plus the MassTransit
// transactional inbox/outbox so the ProcessPaymentRequestedConsumer is exactly-once and the
// PaymentSucceeded/Failed reply commits atomically with the balance change.
public class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }

    public DbSet<PaymentAccount> Accounts => Set<PaymentAccount>();
    public DbSet<PaymentTransaction> Transactions => Set<PaymentTransaction>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PaymentAccount>(e =>
        {
            e.ToTable("payment_accounts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Balance).HasPrecision(18, 2);
            // One account per user; the unique index is what makes "get or create" safe.
            e.HasIndex(a => a.UserId).IsUnique();
            // Balance mutations (deposit, saga-driven debit) run inside an IExecutionStrategy
            // transaction in PaymentService — sufficient for this demo's single-account usage.
            e.HasMany(a => a.Transactions)
                .WithOne(t => t.Account)
                .HasForeignKey(t => t.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PaymentTransaction>(e =>
        {
            e.ToTable("payment_transactions");
            e.HasKey(t => t.Id);
            e.Property(t => t.Amount).HasPrecision(18, 2);
            e.Property(t => t.BalanceAfter).HasPrecision(18, 2);
            // Store the enum as its name string so the column stays human-readable.
            e.Property(t => t.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.Description).HasMaxLength(256);
            e.HasIndex(t => t.AccountId);
            e.HasIndex(t => t.OrderId);
        });

        // MassTransit transactional outbox/inbox (same pattern as OrderDbContext).
        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();
    }
}
