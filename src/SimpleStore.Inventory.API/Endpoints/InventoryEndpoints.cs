namespace SimpleStore.Inventory.API.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // All inventory endpoints are admin-only in v7. No customer-facing reads:
        // the storefront talks to Catalog (which today still exposes Product.Stock).
        // v11: routes resolved as /api/v{version}/inventory/... — see ApiVersioningExtensions.cs.
        var group = app.MapApiV1Group("inventory").RequireAuthorization("Admin");

        group.MapDeliveryNoteEndpoints();
        group.MapReceiptNoteEndpoints();
        group.MapStockEndpoints();

        return app;
    }
}
