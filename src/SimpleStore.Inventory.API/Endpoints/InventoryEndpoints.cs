namespace SimpleStore.Inventory.API.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // All inventory endpoints are admin-only in v7. No customer-facing reads:
        // the storefront talks to Catalog (which today still exposes Product.Stock).
        var group = app.MapGroup("/api/inventory").RequireAuthorization("Admin");

        group.MapDeliveryNoteEndpoints();
        group.MapReceiptNoteEndpoints();
        group.MapStockEndpoints();

        return app;
    }
}
