using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleStore.Inventory.API.Data;

// Lets `dotnet ef migrations add` create migrations without booting Aspire.
public class InventoryReadDbContextDesignTimeFactory : IDesignTimeDbContextFactory<InventoryReadDbContext>
{
    public InventoryReadDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__inventorydb")
            ?? "Host=localhost;Database=inventorydb;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<InventoryReadDbContext>()
            .UseNpgsql(connectionString);

        return new InventoryReadDbContext(optionsBuilder.Options);
    }
}
