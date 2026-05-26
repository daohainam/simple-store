using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data;

namespace SimpleStore.Inventory.API;

public static class InventorySeeder
{
    /// <summary>
    /// Inventory notes are operator-generated; this only applies pending migrations so a
    /// fresh inventorydb is schema-ready. The projector populates the tables from the event
    /// store on first run.
    /// </summary>
    public static async Task SeedAsync(InventoryReadDbContext context)
    {
        await context.Database.MigrateAsync();
    }
}
