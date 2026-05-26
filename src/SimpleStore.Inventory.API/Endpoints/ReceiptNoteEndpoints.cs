using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Application.ReceiptNotes;
using SimpleStore.Inventory.API.Client;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Domain.Shared;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API.Endpoints;

public static class ReceiptNoteEndpoints
{
    public static RouteGroupBuilder MapReceiptNoteEndpoints(this RouteGroupBuilder group)
    {
        var notes = group.MapGroup("/receipt-notes");

        notes.MapPost("", async (
            CreateReceiptNoteRequest request,
            CreateReceiptNoteHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var dto = await handler.HandleAsync(
                    new CreateReceiptNoteCommand(
                        request.Id,
                        request.Date,
                        request.Reference,
                        request.Lines),
                    ct);
                return Results.Created($"/api/inventory/receipt-notes/{dto.Id}", dto);
            }
            catch (DomainException dex)
            {
                return Results.BadRequest(new { error = dex.Message });
            }
            catch (ConcurrencyConflictException)
            {
                return Results.Conflict(new
                {
                    noteId = request.Id,
                    message = "Receipt note with this id has already been recorded.",
                });
            }
        });

        notes.MapGet("", async (
            InventoryReadDbContext db,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.ReceiptNotes.AsNoTracking().OrderByDescending(n => n.RecordedAt);
            var total = await query.CountAsync(ct);
            var rows = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(n => n.Lines)
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<ReceiptNoteDto>
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Items = rows.Select(ToDto).ToList(),
            });
        });

        notes.MapGet("/{id:guid}", async (
            Guid id,
            InventoryReadDbContext db,
            CancellationToken ct) =>
        {
            var row = await db.ReceiptNotes
                .AsNoTracking()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.Id == id, ct);
            return row is null
                ? Results.NotFound()
                : Results.Ok(ToDto(row));
        });

        return group;
    }

    private static ReceiptNoteDto ToDto(Data.ReadModels.ReceiptNoteRow row) => new()
    {
        Id = row.Id,
        Date = row.Date,
        Reference = row.Reference,
        RecordedAt = row.RecordedAt,
        Lines = row.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new InventoryLineDto { ProductId = l.ProductId, Quantity = l.Quantity })
            .ToList(),
    };
}
