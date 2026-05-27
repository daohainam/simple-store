using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Data.ReadModels;
using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;
using SimpleStore.Inventory.API.Domain.Reservations.Events;

namespace SimpleStore.Inventory.API.Projections;

// Pure "event -> SQL (+ integration events)" routines. Stateless; one method per event type.
//
// IDEMPOTENCY: every routine here is safe to re-apply. The header tables key on Id, so a
// duplicate INSERT is detected with AnyAsync and the rest of the apply is skipped. If the entire
// read DB is wiped, the projector replays from $all start and rebuilds everything.
//
// INTEGRATION EVENTS (v8): when an event is LIVE (the subscription has caught up — see
// InventoryProjectionService / EventEnvelope.IsLive), each routine also publishes the matching
// integration event(s) via the MassTransit EF bus outbox. The publish lands in OutboxMessage in
// the SAME Postgres transaction the projection service opened, so the read-model write, the
// checkpoint, and the outbound events all commit atomically. During a cold-start replay isLive is
// false and nothing is published — wiping + replaying does not spam RabbitMQ with history.
public sealed class InventoryProjector
{
    private readonly InventoryReadDbContext _db;
    private readonly IPublishEndpoint _publish;

    public InventoryProjector(InventoryReadDbContext db, IPublishEndpoint publish)
    {
        _db = db;
        _publish = publish;
    }

    public async Task ApplyDeliveryNoteIssuedAsync(DeliveryNoteIssuedV1 evt, bool isLive, CancellationToken ct)
    {
        var exists = await _db.DeliveryNotes.AsNoTracking().AnyAsync(n => n.Id == evt.NoteId, ct);
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
            var newOnHand = await UpsertStockLevelAsync(line.ProductId, -line.Quantity, evt.IssuedAt, ct);
            if (isLive)
                await PublishStockLevelChangedAsync(line.ProductId, newOnHand, evt.IssuedAt, "DeliveryNote", ct);
        }
    }

    public async Task ApplyReceiptNoteRecordedAsync(ReceiptNoteRecordedV1 evt, bool isLive, CancellationToken ct)
    {
        var exists = await _db.ReceiptNotes.AsNoTracking().AnyAsync(n => n.Id == evt.NoteId, ct);
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
            var newOnHand = await UpsertStockLevelAsync(line.ProductId, line.Quantity, evt.RecordedAt, ct);
            if (isLive)
                await PublishStockLevelChangedAsync(line.ProductId, newOnHand, evt.RecordedAt, "ReceiptNote", ct);
        }
    }

    public async Task ApplyStockReservedAsync(StockReservedV1 evt, bool isLive, CancellationToken ct)
    {
        var exists = await _db.Reservations.AsNoTracking().AnyAsync(r => r.Id == evt.NoteId, ct);
        if (exists) return;

        var header = new ReservationRow
        {
            Id = evt.NoteId,
            OrderId = evt.OrderId,
            ReservedAt = evt.ReservedAt,
            Status = "Active",
            LineCount = evt.Lines.Count,
            TotalQuantity = evt.Lines.Sum(l => l.Quantity),
        };

        var lineNumber = 1;
        foreach (var line in evt.Lines)
        {
            header.Lines.Add(new ReservationLineRow
            {
                ReservationId = evt.NoteId,
                LineNumber = lineNumber++,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
            });
        }
        _db.Reservations.Add(header);

        foreach (var line in evt.Lines)
        {
            // Negative delta = stock OUT (reserving removes from available stock).
            _db.StockMovements.Add(new StockMovementRow
            {
                ProductId = line.ProductId,
                Delta = -line.Quantity,
                MovementType = "Reservation",
                SourceNoteId = evt.NoteId,
                OccurredAt = evt.ReservedAt,
            });
            var newOnHand = await UpsertStockLevelAsync(line.ProductId, -line.Quantity, evt.ReservedAt, ct);
            if (isLive)
                await PublishStockLevelChangedAsync(line.ProductId, newOnHand, evt.ReservedAt, "ReservationCreated", ct);
        }

        if (isLive)
        {
            // Tell the checkout saga the reservation succeeded.
            await _publish.Publish(new StockReservedEvent
            {
                CorrelationId = evt.CorrelationId,
                ReservationId = evt.NoteId,
                OrderId = evt.OrderId,
                ReservedAt = evt.ReservedAt,
                Lines = evt.Lines
                    .Select(l => new ReservationLineItem { ProductId = l.ProductId, Quantity = l.Quantity })
                    .ToList(),
            }, ct);
        }
    }

    private async Task PublishStockLevelChangedAsync(
        int productId, int newOnHand, DateTimeOffset at, string cause, CancellationToken ct)
    {
        await _publish.Publish(new StockLevelChangedEvent
        {
            ProductId = productId,
            NewOnHand = newOnHand,
            ChangedAt = at,
            Cause = cause,
        }, ct);
    }

    // Returns the new OnHand after applying the delta, so the caller can publish StockLevelChangedEvent.
    private async Task<int> UpsertStockLevelAsync(int productId, int delta, DateTimeOffset at, CancellationToken ct)
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
            return delta;
        }

        level.OnHand += delta;
        if (at > level.LastMovementAt) level.LastMovementAt = at;
        return level.OnHand;
    }
}
