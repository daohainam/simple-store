using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Data.ReadModels;
using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;

namespace SimpleStore.Inventory.API.Projections;

// Pure "event -> SQL" routines. Stateless; one method per event type.
//
// IDEMPOTENCY: every routine here is safe to re-apply. The header tables key
// on Id, so a duplicate INSERT is detected with FirstOrDefaultAsync and the
// rest of the apply is skipped. The ledger is keyed on (SourceNoteId,
// ProductId) virtually — re-applying inserts duplicate ledger rows in
// theory, but we guard with the same "have I seen this note already?" check.
// If the entire read DB is wiped, the projector replays from $all start and
// rebuilds everything; no manual data migration ever needed.
public sealed class InventoryProjector
{
    private readonly InventoryReadDbContext _db;

    public InventoryProjector(InventoryReadDbContext db) => _db = db;

    public async Task ApplyDeliveryNoteIssuedAsync(DeliveryNoteIssuedV1 evt, CancellationToken ct)
    {
        // Idempotency guard: if we've already projected this note, skip.
        // Cheaper than INSERT ... ON CONFLICT for a small fan-out per event.
        var exists = await _db.DeliveryNotes
            .AsNoTracking()
            .AnyAsync(n => n.Id == evt.NoteId, ct);
        if (exists) return;

        var header = new DeliveryNoteRow
        {
            Id = evt.NoteId,
            Date = evt.Date,
            Reference = evt.Reference,
            IssuedAt = evt.IssuedAt,
            LineCount = evt.Lines.Count,
            TotalQuantity = evt.Lines.Sum(l => l.Quantity),
        };

        var lineNumber = 1;
        foreach (var line in evt.Lines)
        {
            header.Lines.Add(new DeliveryNoteLineRow
            {
                DeliveryNoteId = evt.NoteId,
                LineNumber = lineNumber++,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
            });
        }
        _db.DeliveryNotes.Add(header);

        foreach (var line in evt.Lines)
        {
            // Negative delta = stock OUT.
            _db.StockMovements.Add(new StockMovementRow
            {
                ProductId = line.ProductId,
                Delta = -line.Quantity,
                MovementType = "DeliveryNote",
                SourceNoteId = evt.NoteId,
                OccurredAt = evt.IssuedAt,
            });
            await UpsertStockLevelAsync(line.ProductId, -line.Quantity, evt.IssuedAt, ct);
        }
    }

    public async Task ApplyReceiptNoteRecordedAsync(ReceiptNoteRecordedV1 evt, CancellationToken ct)
    {
        var exists = await _db.ReceiptNotes
            .AsNoTracking()
            .AnyAsync(n => n.Id == evt.NoteId, ct);
        if (exists) return;

        var header = new ReceiptNoteRow
        {
            Id = evt.NoteId,
            Date = evt.Date,
            Reference = evt.Reference,
            RecordedAt = evt.RecordedAt,
            LineCount = evt.Lines.Count,
            TotalQuantity = evt.Lines.Sum(l => l.Quantity),
        };

        var lineNumber = 1;
        foreach (var line in evt.Lines)
        {
            header.Lines.Add(new ReceiptNoteLineRow
            {
                ReceiptNoteId = evt.NoteId,
                LineNumber = lineNumber++,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
            });
        }
        _db.ReceiptNotes.Add(header);

        foreach (var line in evt.Lines)
        {
            // Positive delta = stock IN.
            _db.StockMovements.Add(new StockMovementRow
            {
                ProductId = line.ProductId,
                Delta = line.Quantity,
                MovementType = "ReceiptNote",
                SourceNoteId = evt.NoteId,
                OccurredAt = evt.RecordedAt,
            });
            await UpsertStockLevelAsync(line.ProductId, line.Quantity, evt.RecordedAt, ct);
        }
    }

    private async Task UpsertStockLevelAsync(int productId, int delta, DateTimeOffset at, CancellationToken ct)
    {
        var level = await _db.StockLevels.FirstOrDefaultAsync(s => s.ProductId == productId, ct);
        if (level is null)
        {
            _db.StockLevels.Add(new StockLevelRow
            {
                ProductId = productId,
                OnHand = delta,
                LastMovementAt = at,
            });
        }
        else
        {
            level.OnHand += delta;
            if (at > level.LastMovementAt) level.LastMovementAt = at;
        }
    }
}
