using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleStore.Checkout.API.Data;

// Design-time factory so `dotnet ef migrations add` does NOT have to build the full host.
// Program.cs requires the Aspire-injected 'checkoutdb' connection string (now also needed by the
// Quartz persistent store) which does not exist at design time. EF tooling uses this factory
// instead; the placeholder connection string is never connected to for `migrations add`, and
// migrations are applied at runtime via CheckoutDbContext.Database.MigrateAsync().
public sealed class CheckoutDbContextFactory : IDesignTimeDbContextFactory<CheckoutDbContext>
{
    public CheckoutDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CheckoutDbContext>()
            .UseNpgsql("Host=localhost;Database=checkoutdb")
            .Options;
        return new CheckoutDbContext(options);
    }
}
