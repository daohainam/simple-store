using Microsoft.EntityFrameworkCore;
using SimpleStore.Order.API.Data;

namespace SimpleStore.Order.API;

public static class OrderSeeder
{
    /// <summary>
    /// Orders are user-generated; this only applies pending migrations so a fresh orderdb is schema-ready.
    /// </summary>
    public static async Task SeedAsync(OrderDbContext context)
    {
        await context.Database.MigrateAsync();
    }
}
