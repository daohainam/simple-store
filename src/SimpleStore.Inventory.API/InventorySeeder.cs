using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Domain.ReceiptNotes;
using SimpleStore.Inventory.API.Domain.Shared;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API;

public static class InventorySeeder
{
    // Initial stock per product. These MIRROR SimpleStore.Catalog.API's CatalogSeeder quantities so
    // the two services boot consistent: Catalog's cached Product.Stock equals the Inventory read
    // model's stock_levels. (Seeded stock can't propagate via StockLevelChangedEventV1 because the
    // projector suppresses publishing during the cold-start replay that processes these notes — so
    // both seeders simply agree by construction. Runtime changes DO flow via events.)
    // ProductIds 1..10 are deterministic on a fresh catalogdb (auto-increment insertion order).
    private static readonly (int ProductId, int Quantity)[] SeedStock =
    [
        (1, 50), (2, 30), (3, 20), (4, 40), (5, 100),
        (6, 75), (7, 60), (8, 55), (9, 35), (10, 45),
    ];

    private const int MaxConnectAttempts = 5;

    /// <summary>
    /// Applies pending migrations, then appends one receipt note per seed product to the event store
    /// (the projector turns these into stock_levels rows). Idempotent: deterministic NoteIds + the
    /// read-model fast-path skip + swallowing ConcurrencyConflictException make reruns safe.
    /// </summary>
    public static async Task SeedAsync(
        InventoryReadDbContext context,
        IEventStore eventStore,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct = default)
    {
        await context.Database.MigrateAsync(ct);

        // Fast path: on a warm restart the read model already holds the seed notes — skip entirely.
        if (await context.ReceiptNotes.AnyAsync(ct)) return;

        foreach (var (productId, quantity) in SeedStock)
        {
            var noteId = SeedNoteId(productId);
            var note = ReceiptNote.Record(
                noteId: noteId,
                date: clock.GetUtcNow().UtcDateTime.Date,
                reference: $"SEED-{productId:D3}",
                lines: [new InventoryLine(productId, quantity)],
                now: clock.GetUtcNow());

            await AppendSeedNoteAsync(eventStore, noteId, note, logger, ct);
        }
    }

    private static async Task AppendSeedNoteAsync(
        IEventStore eventStore, Guid noteId, ReceiptNote note, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await eventStore.AppendAsync(
                    $"receiptNote-{noteId}", note.UncommittedEvents, AppendCondition.NoStream, ct);
                return;
            }
            catch (ConcurrencyConflictException)
            {
                // Stream already exists (seeded on a prior run, possibly after a read-DB wipe). Fine.
                return;
            }
            catch (Exception ex) when (attempt < MaxConnectAttempts && !ct.IsCancellationRequested)
            {
                // KurrentDB's gRPC endpoint may need a moment after the container reports ready.
                logger.LogWarning(ex,
                    "Inventory seed append attempt {Attempt}/{Max} failed; retrying.", attempt, MaxConnectAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static Guid SeedNoteId(int productId) =>
        Guid.Parse($"00000000-0000-0000-0000-{productId:D12}");
}
