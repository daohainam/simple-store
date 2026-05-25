using Microsoft.EntityFrameworkCore;
using SimpleStore.Data.Models;

namespace SimpleStore.Data;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Order>(e =>
        {
            e.Property(o => o.TotalAmount).HasPrecision(18, 2);
        });

        builder.Entity<OrderItem>(e =>
        {
            e.Property(oi => oi.UnitPrice).HasPrecision(18, 2);
            e.Property(oi => oi.ProductName).HasMaxLength(200);
        });
    }
}
