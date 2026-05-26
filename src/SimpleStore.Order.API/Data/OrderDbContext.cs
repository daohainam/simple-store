using Microsoft.EntityFrameworkCore;
using OrderEntity = SimpleStore.Order.API.Models.Order;
using OrderItem = SimpleStore.Order.API.Models.OrderItem;

namespace SimpleStore.Order.API.Data;

// Alias the entity to avoid the SimpleStore.Order namespace shadowing the Models.Order type
// (Order entity name collides with the second segment of this project's namespace tree).
public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<OrderEntity> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<OrderEntity>(e =>
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
