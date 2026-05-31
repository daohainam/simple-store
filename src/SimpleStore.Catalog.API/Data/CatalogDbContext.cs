using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Catalog.API.Models;

namespace SimpleStore.Catalog.API.Data;

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; }
    public DbSet<Category> Categories { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Product>(e =>
        {
            e.Property(p => p.Price).HasPrecision(18, 2);
            // Explicit index on the FK column so category-filtered product queries use an index scan.
            e.HasIndex(p => p.CategoryId).HasDatabaseName("ix_products_category_id");
        });

        // MassTransit outbox (publish ProductUpdatedEventV1) + inbox (idempotent consume of
        // OrderSubmittedEventV1 — without this, a redelivered message would decrement Product.Stock twice).
        builder.AddInboxStateEntity();
        builder.AddOutboxMessageEntity();
        builder.AddOutboxStateEntity();
    }
}
