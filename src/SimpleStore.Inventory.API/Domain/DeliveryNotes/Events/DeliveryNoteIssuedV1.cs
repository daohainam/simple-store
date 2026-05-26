namespace SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;

// Domain event emitted when a delivery note is first issued (stock OUT).
//
// Wire type string: "simplestore.inventory.delivery-note.issued.v1".
// The trailing .v1 is the versioning anchor: additive changes (new optional field)
// keep .v1 and rely on JSON's defaulting behavior; incompatible changes go to .v2
// and the projector handles both.
//
// Date    = business date of the delivery (DateTime, midnight UTC by convention).
// IssuedAt = audit instant the note was issued (DateTimeOffset, explicit zone).
// See CLAUDE.md for the project-wide DateTime / DateTimeOffset split.
public sealed record DeliveryNoteIssuedV1 : IInventoryDomainEvent
{
    public Guid NoteId { get; init; }
    public DateTime Date { get; init; }
    public string? Reference { get; init; }
    public IReadOnlyList<LineData> Lines { get; init; } = [];
    public DateTimeOffset IssuedAt { get; init; }

    public sealed record LineData
    {
        public int ProductId { get; init; }
        public int Quantity { get; init; }
    }
}
