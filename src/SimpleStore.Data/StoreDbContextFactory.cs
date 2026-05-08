using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace SimpleStore.Data;

public class StoreDbContextFactory : IDesignTimeDbContextFactory<StoreDbContext>
{
    public StoreDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile(Path.Combine("..", "SimpleStore.Web", "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine("..", "SimpleStore.Web", $"appsettings.{environment}.json"), optional: true)
            .AddJsonFile(Path.Combine("..", "SimpleStore.Admin", "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine("..", "SimpleStore.Admin", $"appsettings.{environment}.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("storedb")
            ?? configuration["ConnectionStrings:storedb"]
            ?? throw new InvalidOperationException("Connection string 'storedb' was not found. Configure ConnectionStrings:storedb for EF Core design-time operations.");

        var optionsBuilder = new DbContextOptionsBuilder<StoreDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new StoreDbContext(optionsBuilder.Options);
    }
}
