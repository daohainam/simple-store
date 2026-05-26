using SimpleStore.Inventory.API.Client;
using SimpleStore.Inventory.API.Domain.ReceiptNotes;
using SimpleStore.Inventory.API.Domain.Shared;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API.Application.ReceiptNotes;

// Mirror of CreateDeliveryNoteHandler. See that file's header for the full
// CQRS-flow narrative — the only differences here are aggregate (ReceiptNote)
// and stream-name prefix ("receiptNote-").
public sealed class CreateReceiptNoteHandler
{
    private readonly IEventStore _eventStore;
    private readonly TimeProvider _clock;

    public CreateReceiptNoteHandler(IEventStore eventStore, TimeProvider clock)
    {
        _eventStore = eventStore;
        _clock = clock;
    }

    public async Task<ReceiptNoteDto> HandleAsync(CreateReceiptNoteCommand cmd, CancellationToken ct)
    {
        if (cmd.Lines is null || cmd.Lines.Count == 0)
            throw new DomainException("A receipt note must have at least one line.");

        var domainLines = cmd.Lines
            .Select(l => new InventoryLine(l.ProductId, l.Quantity))
            .ToList();

        var note = ReceiptNote.Record(
            noteId: cmd.NoteId,
            date: cmd.Date,
            reference: cmd.Reference,
            lines: domainLines,
            now: _clock.GetUtcNow());

        var stream = $"receiptNote-{note.Id}";
        await _eventStore.AppendAsync(stream, note.UncommittedEvents, AppendCondition.NoStream, ct);
        note.MarkEventsCommitted();

        return new ReceiptNoteDto
        {
            Id = note.Id,
            Date = note.Date,
            Reference = note.Reference,
            RecordedAt = note.RecordedAt,
            Lines = note.Lines
                .Select(l => new InventoryLineDto { ProductId = l.ProductId, Quantity = l.Quantity })
                .ToList(),
        };
    }
}
