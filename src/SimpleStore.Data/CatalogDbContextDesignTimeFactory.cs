using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleStore.Data;

public class CatalogDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__catalogdb")
            ?? "Host=localhost;Database=catalogdb;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString);

        return new CatalogDbContext(optionsBuilder.Options);
    }
}
