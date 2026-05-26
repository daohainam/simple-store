using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Data.ReadModels;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API.Projections.Checkpoints;

// Reads/writes the projector's bookmark into the inventorydb projection_checkpoints
// table. KurrentDB exposes the $all position as a (commit, prepare) ulong pair;
// Postgres has no native ulong, so we store both as bigint (signed). Bit patterns
// round-trip through (long)(ulong) casts.
public sealed class CheckpointStore
{
    private readonly InventoryReadDbContext _db;

    public CheckpointStore(InventoryReadDbContext db) => _db = db;

    public async Task<EventStorePosition?> LoadAsync(string projectionName, CancellationToken ct)
    {
        var row = await _db.ProjectionCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectionName == projectionName, ct);
        if (row is null) return null;

        return new EventStorePosition(
            CommitPosition: unchecked((ulong)row.CommitPosition),
            PreparePosition: unchecked((ulong)row.PreparePosition));
    }

    public async Task UpsertAsync(
        string projectionName,
        EventStorePosition position,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var existing = await _db.ProjectionCheckpoints
            .FirstOrDefaultAsync(c => c.ProjectionName == projectionName, ct);

        if (existing is null)
        {
            _db.ProjectionCheckpoints.Add(new ProjectionCheckpointRow
            {
                ProjectionName = projectionName,
                CommitPosition = unchecked((long)position.CommitPosition),
                PreparePosition = unchecked((long)position.PreparePosition),
                UpdatedAt = updatedAt,
            });
        }
        else
        {
            existing.CommitPosition = unchecked((long)position.CommitPosition);
            existing.PreparePosition = unchecked((long)position.PreparePosition);
            existing.UpdatedAt = updatedAt;
        }
    }
}
