using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Checkout.API.Sagas;

namespace SimpleStore.Checkout.API.Data;

// Persists the checkout saga state plus the MassTransit transactional inbox/outbox. The EF saga
// repository (configured in Program.cs) reads and writes CheckoutSagaState through this context.
public class CheckoutDbContext : DbContext
{
    public CheckoutDbContext(DbContextOptions<CheckoutDbContext> options) : base(options) { }

    public DbSet<CheckoutSagaState> Sagas => Set<CheckoutSagaState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<CheckoutSagaState>(e =>
        {
            e.ToTable("checkout_saga_state");
            e.HasKey(x => x.CorrelationId);
            e.Property(x => x.CorrelationId).ValueGeneratedNever();
            e.Property(x => x.CurrentState).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(256);
            e.Property(x => x.FailureReason).HasMaxLength(128);
            e.HasIndex(x => x.OrderId);
        });

        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();
    }
}
