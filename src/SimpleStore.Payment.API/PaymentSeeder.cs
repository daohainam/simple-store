using Microsoft.EntityFrameworkCore;
using SimpleStore.Payment.API.Data;

namespace SimpleStore.Payment.API;

public static class PaymentSeeder
{
    /// <summary>
    /// Accounts are user-driven (auto-provisioned on first access) and keyed by Identity's GUID
    /// user ids, which aren't known at seed time — so this only applies pending migrations.
    /// </summary>
    public static async Task SeedAsync(PaymentDbContext context)
    {
        await context.Database.MigrateAsync();
    }
}
