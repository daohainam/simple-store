using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Client;
using SimpleStore.Inventory.API.Data;

namespace SimpleStore.Inventory.API.Endpoints;

public static class StockEndpoints
{
    public static RouteGroupBuilder MapStockEndpoints(this RouteGroupBuilder group)
    {
        var stock = group.MapGroup("/stock");

        // Paged list of every product with at least one movement on file.
        // Products that have never been touched aren't in this table; they
        // 404 from GET /stock/{productId}. We don't pretend OnHand=0 because
        // we don't actually know whether the product exists.
        stock.MapGet("", async (
            InventoryReadDbContext db,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.StockLevels.AsNoTracking().OrderBy(s => s.ProductId);
            var total = await query.CountAsync(ct);
            var rows = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<StockLevelDto>
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Items = rows.Select(r => new StockLevelDto
                {
                    ProductId = r.ProductId,
                    OnHand = r.OnHand,
                    LastMovementAt = r.LastMovementAt,
                }).ToList(),
            });
        });

        stock.MapGet("/{productId:int}", async (
            int productId,
            InventoryReadDbContext db,
            CancellationToken ct) =>
        {
            var row = await db.StockLevels
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ProductId == productId, ct);
            if (row is null)
                return Results.NotFound(new { productId, message = "No movements recorded for this product." });

            return Results.Ok(new StockLevelDto
            {
                ProductId = row.ProductId,
                OnHand = row.OnHand,
                LastMovementAt = row.LastMovementAt,
            });
        });

        stock.MapGet("/{productId:int}/movements", async (
            int productId,
            InventoryReadDbContext db,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.StockMovements
                .AsNoTracking()
                .Where(m => m.ProductId == productId)
                .OrderByDescending(m => m.OccurredAt)
                .ThenByDescending(m => m.Id);
            var total = await query.CountAsync(ct);
            var rows = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<StockMovementDto>
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Items = rows.Select(r => new StockMovementDto
                {
                    Id = r.Id,
                    ProductId = r.ProductId,
                    Delta = r.Delta,
                    MovementType = r.MovementType,
                    SourceNoteId = r.SourceNoteId,
                    OccurredAt = r.OccurredAt,
                }).ToList(),
            });
        });

        return group;
    }
}
