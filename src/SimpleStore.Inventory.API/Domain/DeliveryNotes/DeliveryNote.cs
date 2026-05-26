using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.Shared;

namespace SimpleStore.Inventory.API.Domain.DeliveryNotes;

// DDD aggregate root: stock-OUT document (e.g. goods shipped to a customer).
//
// State changes are expressed as domain events. The public surface is:
//   - Issue(...)               factory that creates a new aggregate + emits DeliveryNoteIssuedV1.
//   - Rehydrate(events)        rebuilds an aggregate from a stream of past events (used by repositories).
//   - UncommittedEvents        events the application layer must persist.
//   - MarkEventsCommitted()    called after the event store accepts the append.
//
// The aggregate cannot be issued twice; rehydration rejects a duplicate Issued event.
// At the store level this is enforced by appending with StreamState.NoStream.
public sealed class DeliveryNote
{
    public Guid Id { get; private set; }
    public DateTime Date { get; private set; }
    public string? Reference { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public IReadOnlyList<InventoryLine> Lines => _lines;

    private readonly List<InventoryLine> _lines = [];
    private readonly List<IInventoryDomainEvent> _uncommitted = [];
    private bool _issued;

    public IReadOnlyList<IInventoryDomainEvent> UncommittedEvents => _uncommitted;
    public void MarkEventsCommitted() => _uncommitted.Clear();

    private DeliveryNote() { }

    public static DeliveryNote Issue(
        Guid noteId,
        DateTime? date,
        string? reference,
        IReadOnlyList<InventoryLine> lines,
        DateTimeOffset now)
    {
        if (noteId == Guid.Empty)
            throw new DomainException("Delivery note id must be a non-empty Guid.");
        if (lines is null || lines.Count == 0)
            throw new DomainException("A delivery note must have at least one line.");

        var deduped = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            if (deduped.ContainsKey(line.ProductId))
                throw new DomainException(
                    $"Duplicate ProductId {line.ProductId} on the same delivery note. " +
                    "Collapse into a single line.");
            deduped[line.ProductId] = line.Quantity;
        }

        var businessDate = (date ?? now.UtcDateTime).Date;

        var evt = new DeliveryNoteIssuedV1
        {
            NoteId = noteId,
            Date = DateTime.SpecifyKind(businessDate, DateTimeKind.Utc),
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            IssuedAt = now,
            Lines = lines
                .Select(l => new DeliveryNoteIssuedV1.LineData
                {
                    ProductId = l.ProductId,
                    Quantity = l.Quantity
                })
                .ToList()
        };

        var note = new DeliveryNote();
        note.Apply(evt);
        note._uncommitted.Add(evt);
        return note;
    }

    public static DeliveryNote Rehydrate(IEnumerable<IInventoryDomainEvent> events)
    {
        var note = new DeliveryNote();
        foreach (var evt in events) note.Apply(evt);
        if (!note._issued)
            throw new DomainException("Delivery note stream did not contain an Issued event.");
        return note;
    }

    private void Apply(IInventoryDomainEvent @event)
    {
        switch (@event)
        {
            case DeliveryNoteIssuedV1 issued:
                if (_issued)
                    throw new DomainException("Delivery note has already been issued.");
                Id = issued.NoteId;
                Date = issued.Date;
                Reference = issued.Reference;
                IssuedAt = issued.IssuedAt;
                _lines.AddRange(issued.Lines.Select(l => new InventoryLine(l.ProductId, l.Quantity)));
                _issued = true;
                break;
            default:
                throw new DomainException(
                    $"DeliveryNote cannot apply event of type {@event.GetType().Name}.");
        }
    }
}
