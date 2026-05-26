using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Application.DeliveryNotes;
using SimpleStore.Inventory.API.Client;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Domain.Shared;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API.Endpoints;

public static class DeliveryNoteEndpoints
{
    public static RouteGroupBuilder MapDeliveryNoteEndpoints(this RouteGroupBuilder group)
    {
        var notes = group.MapGroup("/delivery-notes");

        // POST returns the DTO from the in-memory aggregate state, NOT the read DB.
        // The projector is async; reading back through GET microseconds later may
        // briefly 404 while the projector catches up. That's the eventual-consistency
        // demo this design is meant to teach.
        notes.MapPost("", async (
            CreateDeliveryNoteRequest request,
            CreateDeliveryNoteHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var dto = await handler.HandleAsync(
                    new CreateDeliveryNoteCommand(
                        request.Id,
                        request.Date,
                        request.Reference,
                        request.Lines),
                    ct);
                return Results.Created($"/api/inventory/delivery-notes/{dto.Id}", dto);
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
                    message = "Delivery note with this id has already been issued.",
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

            var query = db.DeliveryNotes.AsNoTracking().OrderByDescending(n => n.IssuedAt);
            var total = await query.CountAsync(ct);
            var rows = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Include(n => n.Lines)
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<DeliveryNoteDto>
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
            var row = await db.DeliveryNotes
                .AsNoTracking()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.Id == id, ct);
            return row is null
                ? Results.NotFound()
                : Results.Ok(ToDto(row));
        });

        return group;
    }

    private static DeliveryNoteDto ToDto(Data.ReadModels.DeliveryNoteRow row) => new()
    {
        Id = row.Id,
        Date = row.Date,
        Reference = row.Reference,
        IssuedAt = row.IssuedAt,
        Lines = row.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new InventoryLineDto { ProductId = l.ProductId, Quantity = l.Quantity })
            .ToList(),
    };
}
