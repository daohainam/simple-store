using System.Security.Claims;
using SimpleStore.Payment.API.Client;
using SimpleStore.Payment.API.Services;

namespace SimpleStore.Payment.API.Endpoints;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        // v12: routes resolved as /api/v{version}/payment/... — see ApiVersioningExtensions.cs.
        var group = app.MapApiV1Group("payment");

        MapUserEndpoints(group);
        MapAdminEndpoints(group);

        return app;
    }

    private static void MapUserEndpoints(RouteGroupBuilder group)
    {
        var account = group.MapGroup("/account").RequireAuthorization();

        // Returns (auto-provisioning at zero balance) the caller's account.
        account.MapGet("", async (ClaimsPrincipal user, IPaymentService service, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return Results.Ok(await service.GetOrCreateAccountAsync(userId, ct));
        });

        account.MapPost("/deposit", async (DepositRequest request, ClaimsPrincipal user, IPaymentService service, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            if (request.Amount <= 0) return Results.BadRequest("Deposit amount must be positive.");
            return Results.Ok(await service.DepositAsync(userId, request.Amount, ct));
        });

        account.MapGet("/transactions", async (ClaimsPrincipal user, IPaymentService service, CancellationToken ct) =>
        {
            var userId = user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
            return Results.Ok(await service.GetTransactionsAsync(userId, ct));
        });
    }

    private static void MapAdminEndpoints(RouteGroupBuilder group)
    {
        var admin = group.MapGroup("/admin/accounts").RequireAuthorization("Admin");

        admin.MapGet("", async (IPaymentService service, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
            Results.Ok(await service.GetAccountsAsync(page, pageSize, ct)));

        admin.MapGet("/count", async (IPaymentService service, CancellationToken ct) =>
            Results.Ok(await service.GetAccountCountAsync(ct)));

        admin.MapGet("/{userId}", async (string userId, IPaymentService service, CancellationToken ct) =>
        {
            var dto = await service.GetAccountByUserAsync(userId, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        admin.MapPost("/{userId}/deposit", async (string userId, DepositRequest request, IPaymentService service, CancellationToken ct) =>
        {
            if (request.Amount <= 0) return Results.BadRequest("Deposit amount must be positive.");
            return Results.Ok(await service.DepositAsync(userId, request.Amount, ct));
        });

        admin.MapGet("/{userId}/transactions", async (string userId, IPaymentService service, CancellationToken ct) =>
            Results.Ok(await service.GetTransactionsAsync(userId, ct)));
    }
}
