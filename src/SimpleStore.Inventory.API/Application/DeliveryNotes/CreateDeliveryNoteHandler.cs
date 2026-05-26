using SimpleStore.Inventory.API.Client;
using SimpleStore.Inventory.API.Domain.DeliveryNotes;
using SimpleStore.Inventory.API.Domain.Shared;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API.Application.DeliveryNotes;

// CQRS write-side handler. No MediatR — plain DI service with a single async method.
// Flow:
//   1. Validate command (lines non-empty; the aggregate enforces Quantity > 0 and dedupe).
//   2. Construct the aggregate via DeliveryNote.Issue(...) — emits DeliveryNoteIssuedV1.
//   3. AppendAsync with AppendCondition.NoStream. A retried POST with the same NoteId
//      hits this branch and the store rejects it -> ConcurrencyConflictException -> 409.
//   4. Mark events committed.
//   5. Return a DTO built from the in-memory aggregate state — NOT the read DB
//      (the projector is async; a GET issued microseconds later may briefly 404).
public sealed class CreateDeliveryNoteHandler
{
    private readonly IEventStore _eventStore;
    private readonly TimeProvider _clock;

    public CreateDeliveryNoteHandler(IEventStore eventStore, TimeProvider clock)
    {
        _eventStore = eventStore;
        _clock = clock;
    }

    public async Task<DeliveryNoteDto> HandleAsync(CreateDeliveryNoteCommand cmd, CancellationToken ct)
    {
        if (cmd.Lines is null || cmd.Lines.Count == 0)
            throw new DomainException("A delivery note must have at least one line.");

        var domainLines = cmd.Lines
            .Select(l => new InventoryLine(l.ProductId, l.Quantity))
            .ToList();

        var note = DeliveryNote.Issue(
            noteId: cmd.NoteId,
            date: cmd.Date,
            reference: cmd.Reference,
            lines: domainLines,
            now: _clock.GetUtcNow());

        var stream = $"deliveryNote-{note.Id}";
        await _eventStore.AppendAsync(stream, note.UncommittedEvents, AppendCondition.NoStream, ct);
        note.MarkEventsCommitted();

        return new DeliveryNoteDto
        {
            Id = note.Id,
            Date = note.Date,
            Reference = note.Reference,
            IssuedAt = note.IssuedAt,
            Lines = note.Lines
                .Select(l => new InventoryLineDto { ProductId = l.ProductId, Quantity = l.Quantity })
                .ToList(),
        };
    }
}
