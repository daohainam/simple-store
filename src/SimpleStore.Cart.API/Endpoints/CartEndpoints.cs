using SimpleStore.Cart.API.Client;
using SimpleStore.Cart.API.Services;

namespace SimpleStore.Cart.API.Endpoints;

public static class CartEndpoints
{
    private const string CartIdHeader = "X-Cart-Id";

    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cart");

        // Cart read/write — open to anonymous (owner key falls back to X-Cart-Id header).
        group.MapGet("", async (HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.BadRequest("Missing cart owner: authenticate or send X-Cart-Id.");
            return Results.Ok(await store.GetAsync(owner, ct));
        }).AllowAnonymous();

        group.MapPost("/items", async (AddCartItemRequest request, HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.BadRequest("Missing cart owner: authenticate or send X-Cart-Id.");
            return Results.Ok(await store.AddItemAsync(owner, request, ct));
        }).AllowAnonymous();

        group.MapPut("/items/{productId:int}", async (int productId, UpdateCartItemRequest request, HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.BadRequest("Missing cart owner: authenticate or send X-Cart-Id.");
            return Results.Ok(await store.UpdateItemAsync(owner, productId, request.Quantity, ct));
        }).AllowAnonymous();

        group.MapDelete("/items/{productId:int}", async (int productId, HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.BadRequest("Missing cart owner: authenticate or send X-Cart-Id.");
            return Results.Ok(await store.RemoveItemAsync(owner, productId, ct));
        }).AllowAnonymous();

        group.MapDelete("", async (HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.BadRequest("Missing cart owner: authenticate or send X-Cart-Id.");
            await store.ClearAsync(owner, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/count", async (HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.Ok(0);
            var cart = await store.GetAsync(owner, ct);
            return Results.Ok(cart.Count);
        }).AllowAnonymous();

        group.MapGet("/total", async (HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var owner = ResolveOwner(ctx);
            if (owner is null) return Results.Ok(0m);
            var cart = await store.GetAsync(owner, ct);
            return Results.Ok(cart.Total);
        }).AllowAnonymous();

        // Merge requires a JWT — the destination is the authenticated user's cart.
        group.MapPost("/merge", async (MergeCartRequest request, HttpContext ctx, ICartStore store, CancellationToken ct) =>
        {
            var sub = ctx.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
            if (string.IsNullOrEmpty(request.AnonymousCartId)) return Results.BadRequest("AnonymousCartId is required.");

            await store.MergeAsync($"anon:{request.AnonymousCartId}", $"user:{sub}", ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static string? ResolveOwner(HttpContext ctx)
    {
        var sub = ctx.User.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return $"user:{sub}";

        var anon = ctx.Request.Headers[CartIdHeader].ToString();
        return string.IsNullOrEmpty(anon) ? null : $"anon:{anon}";
    }
}
