using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace SimpleStore.Data;

// Used only by `dotnet ef` at design time. Configures IdentityOptions so that
// IdentityDbContext.OnModelCreating sees SchemaVersion = Version3 and emits
// the passkey entity into the model. See dotnet/efcore#36314.
public class StoreDbContextDesignTimeFactory : IDesignTimeDbContextFactory<StoreDbContext>
{
    public StoreDbContext CreateDbContext(string[] args)
    {
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        });
        var appServices = services.BuildServiceProvider();

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__storedb")
            ?? "Host=localhost;Database=storedb;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(appServices);

        return new StoreDbContext(optionsBuilder.Options);
    }
}
