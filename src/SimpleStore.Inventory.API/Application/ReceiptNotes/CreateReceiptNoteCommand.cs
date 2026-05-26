using SimpleStore.Inventory.API.Client;

namespace SimpleStore.Inventory.API.Application.ReceiptNotes;

public sealed record CreateReceiptNoteCommand(
    Guid NoteId,
    DateTime? Date,
    string? Reference,
    IReadOnlyList<InventoryLineDto> Lines);
