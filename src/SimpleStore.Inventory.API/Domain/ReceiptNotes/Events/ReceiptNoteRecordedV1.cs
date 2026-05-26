namespace SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;

// Domain event emitted when a receipt note is recorded (stock IN).
// Wire type string: "simplestore.inventory.receipt-note.recorded.v1".
// Mirrors DeliveryNoteIssuedV1; the asymmetric verb ("recorded" vs "issued")
// reflects how operations people actually talk about the two documents and
// resists the temptation to share a base record.
public sealed record ReceiptNoteRecordedV1 : IInventoryDomainEvent
{
    public Guid NoteId { get; init; }
    public DateTime Date { get; init; }
    public string? Reference { get; init; }
    public IReadOnlyList<LineData> Lines { get; init; } = [];
    public DateTimeOffset RecordedAt { get; init; }

    public sealed record LineData
    {
        public int ProductId { get; init; }
        public int Quantity { get; init; }
    }
}
