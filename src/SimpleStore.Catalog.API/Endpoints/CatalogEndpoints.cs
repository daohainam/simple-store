using SimpleStore.Catalog.API.Client;
using SimpleStore.Catalog.API.Services;

namespace SimpleStore.Catalog.API.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog");

        MapProductEndpoints(group);
        MapCategoryEndpoints(group);

        return app;
    }

    private static void MapProductEndpoints(RouteGroupBuilder group)
    {
        var products = group.MapGroup("/products");

        products.MapGet("", async (
            ICatalogService service,
            int page = 1,
            int pageSize = 20,
            int? categoryId = null,
            string? search = null,
            CancellationToken ct = default) =>
        {
            var result = await service.GetProductsAsync(page, pageSize, categoryId, search, ct);
            return Results.Ok(result);
        });

        products.MapGet("/count", async (ICatalogService service, CancellationToken ct) =>
            Results.Ok(await service.GetProductCountAsync(ct)));

        products.MapGet("/{id:int}", async (int id, ICatalogService service, CancellationToken ct) =>
        {
            var dto = await service.GetProductByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        products.MapPost("", async (CreateProductRequest request, ICatalogService service, CancellationToken ct) =>
        {
            var created = await service.CreateProductAsync(request, ct);
            return Results.Created($"/api/catalog/products/{created.Id}", created);
        }).RequireAuthorization("Admin");

        products.MapPut("/{id:int}", async (int id, UpdateProductRequest request, ICatalogService service, CancellationToken ct) =>
        {
            var updated = await service.UpdateProductAsync(id, request, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Admin");

        products.MapDelete("/{id:int}", async (int id, ICatalogService service, CancellationToken ct) =>
        {
            var deleted = await service.DeleteProductAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Admin");
    }

    private static void MapCategoryEndpoints(RouteGroupBuilder group)
    {
        var categories = group.MapGroup("/categories");

        categories.MapGet("", async (
            ICatalogService service,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            var result = await service.GetCategoriesAsync(page, pageSize, ct);
            return Results.Ok(result);
        });

        categories.MapGet("/count", async (ICatalogService service, CancellationToken ct) =>
            Results.Ok(await service.GetCategoryCountAsync(ct)));

        categories.MapGet("/{id:int}", async (int id, ICatalogService service, CancellationToken ct) =>
        {
            var dto = await service.GetCategoryByIdAsync(id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        categories.MapPost("", async (CategoryDto dto, ICatalogService service, CancellationToken ct) =>
        {
            var created = await service.CreateCategoryAsync(dto, ct);
            return Results.Created($"/api/catalog/categories/{created.Id}", created);
        }).RequireAuthorization("Admin");

        categories.MapPut("/{id:int}", async (int id, CategoryDto dto, ICatalogService service, CancellationToken ct) =>
        {
            var updated = await service.UpdateCategoryAsync(id, dto, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Admin");

        categories.MapDelete("/{id:int}", async (int id, ICatalogService service, CancellationToken ct) =>
        {
            var result = await service.DeleteCategoryAsync(id, ct);
            return result switch
            {
                DeleteCategoryResult.Deleted => Results.NoContent(),
                DeleteCategoryResult.NotFound => Results.NotFound(),
                DeleteCategoryResult.HasProducts => Results.Conflict(new { error = "Category still has products." }),
                _ => Results.StatusCode(500)
            };
        }).RequireAuthorization("Admin");
    }
}
