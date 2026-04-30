using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Data.Identity;
using SimpleStore.Data.Models;

namespace SimpleStore.Data;

public class StoreDbContext : IdentityDbContext<ApplicationUser>
{
    public StoreDbContext(DbContextOptions<StoreDbContext> options) : base(options) { }
    
    public DbSet<Product> Products { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }
    
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        
        builder.Entity<Product>(e => {
            e.Property(p => p.Price).HasPrecision(18, 2);
        });
        
        builder.Entity<OrderItem>(e => {
            e.Property(oi => oi.UnitPrice).HasPrecision(18, 2);
        });
        
        builder.Entity<Order>(e => {
            e.Property(o => o.TotalAmount).HasPrecision(18, 2);
        });
    }
}
