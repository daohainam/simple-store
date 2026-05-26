using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;
using SimpleStore.Inventory.API.Domain.Shared;

namespace SimpleStore.Inventory.API.Domain.ReceiptNotes;

// DDD aggregate root: stock-IN document (e.g. goods received from a supplier).
//
// Mirror of DeliveryNote. The two aggregates are deliberately separate even though
// their shapes are structurally similar — in v8 a receipt note will likely grow
// supplier / purchase-order fields that a delivery note doesn't have.
public sealed class ReceiptNote
{
    public Guid Id { get; private set; }
    public DateTime Date { get; private set; }
    public string? Reference { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public IReadOnlyList<InventoryLine> Lines => _lines;

    private readonly List<InventoryLine> _lines = [];
    private readonly List<IInventoryDomainEvent> _uncommitted = [];
    private bool _recorded;

    public IReadOnlyList<IInventoryDomainEvent> UncommittedEvents => _uncommitted;
    public void MarkEventsCommitted() => _uncommitted.Clear();

    private ReceiptNote() { }

    public static ReceiptNote Record(
        Guid noteId,
        DateTime? date,
        string? reference,
        IReadOnlyList<InventoryLine> lines,
        DateTimeOffset now)
    {
        if (noteId == Guid.Empty)
            throw new DomainException("Receipt note id must be a non-empty Guid.");
        if (lines is null || lines.Count == 0)
            throw new DomainException("A receipt note must have at least one line.");

        var deduped = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            if (deduped.ContainsKey(line.ProductId))
                throw new DomainException(
                    $"Duplicate ProductId {line.ProductId} on the same receipt note. " +
                    "Collapse into a single line.");
            deduped[line.ProductId] = line.Quantity;
        }

        var businessDate = (date ?? now.UtcDateTime).Date;

        var evt = new ReceiptNoteRecordedV1
        {
            NoteId = noteId,
            Date = DateTime.SpecifyKind(businessDate, DateTimeKind.Utc),
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            RecordedAt = now,
            Lines = lines
                .Select(l => new ReceiptNoteRecordedV1.LineData
                {
                    ProductId = l.ProductId,
                    Quantity = l.Quantity
                })
                .ToList()
        };

        var note = new ReceiptNote();
        note.Apply(evt);
        note._uncommitted.Add(evt);
        return note;
    }

    public static ReceiptNote Rehydrate(IEnumerable<IInventoryDomainEvent> events)
    {
        var note = new ReceiptNote();
        foreach (var evt in events) note.Apply(evt);
        if (!note._recorded)
            throw new DomainException("Receipt note stream did not contain a Recorded event.");
        return note;
    }

    private void Apply(IInventoryDomainEvent @event)
    {
        switch (@event)
        {
            case ReceiptNoteRecordedV1 recorded:
                if (_recorded)
                    throw new DomainException("Receipt note has already been recorded.");
                Id = recorded.NoteId;
                Date = recorded.Date;
                Reference = recorded.Reference;
                RecordedAt = recorded.RecordedAt;
                _lines.AddRange(recorded.Lines.Select(l => new InventoryLine(l.ProductId, l.Quantity)));
                _recorded = true;
                break;
            default:
                throw new DomainException(
                    $"ReceiptNote cannot apply event of type {@event.GetType().Name}.");
        }
    }
}
