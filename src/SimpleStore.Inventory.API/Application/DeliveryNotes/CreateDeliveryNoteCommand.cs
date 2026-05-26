using SimpleStore.Inventory.API.Client;

namespace SimpleStore.Inventory.API.Application.DeliveryNotes;

// Application-layer command. Carries the user's intent into the write side.
// NoteId is client-supplied so retries collapse onto the same event-store stream.
public sealed record CreateDeliveryNoteCommand(
    Guid NoteId,
    DateTime? Date,
    string? Reference,
    IReadOnlyList<InventoryLineDto> Lines);
