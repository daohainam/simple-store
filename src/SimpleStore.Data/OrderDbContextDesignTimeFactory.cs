using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleStore.Data;

public class OrderDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__orderdb")
            ?? "Host=localhost;Database=orderdb;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(connectionString);

        return new OrderDbContext(optionsBuilder.Options);
    }
}
