using System.Security.Claims;
using SimpleStore.Order.API.Client;
using SimpleStore.Order.API.Services;

namespace SimpleStore.Order.API.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // v11: routes resolved as /api/v{version}/order/... — see ApiVersioningExtensions.cs.
        var group = app.MapApiV1Group("order");

        MapUserEndpoints(group);
        MapAdminEndpoints(group);

        return app;
    }

    private static void MapUserEndpoints(RouteGroupBuilder group)
    {
        var orders = group.MapGroup("/orders").RequireAuthorization();

        orders.MapGet("", async (ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var result = await service.GetMyOrdersAsync(userId, ct);
            return Results.Ok(result);
        });

        orders.MapGet("/{id:int}", async (int id, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var dto = await service.GetMyOrderByIdAsync(id, userId, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        orders.MapPost("", async (CreateOrderRequest request, ClaimsPrincipal user, IOrderService service, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            var created = await service.CreateOrderAsync(userId, request, ct);
            return Results.Created($"/api/order/orders/{created.Id}", created);
        });
    }

    private static void MapAdminEndpoints(RouteGroupBuilder group)
    {
        var admin = group.MapGroup("/admin/orders").RequireAuthorization("Admin");

        admin.MapGet("", async (IOrderService service, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        {
            var result = await service.GetOrdersAsync(page, pageSize, ct);
            return Results.Ok(result);
        });

        admin.MapGet("/count", async (IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetOrderCountAsync(ct)));

        admin.MapGet("/{id:int}", async (int id, IOrderService service, CancellationToken ct) =>
        {
            var dto = await service.GetOrderByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        admin.MapPatch("/{id:int}/status", async (int id, UpdateOrderStatusRequest request, IOrderService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Status)) return Results.BadRequest("Status is required.");
            var updated = await service.UpdateStatusAsync(id, request.Status, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        });

        // /admin/orders/stats lives alongside the other admin endpoints — keeps the admin surface in one place.
        admin.MapGet("/stats", async (IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatsAsync(ct)));

        // Bulk per-user order counts — replaces the EF GroupBy that Admin's Customers page used to run directly.
        admin.MapGet("/counts-by-user", async (IOrderService service, CancellationToken ct) =>
            Results.Ok(await service.GetOrderCountsByUserAsync(ct)));
    }
}
