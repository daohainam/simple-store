using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Identity.API.Client;
using SimpleStore.Identity.API.Data;
using SimpleStore.Identity.API.Models;
using SimpleStore.Identity.API.Services;

namespace SimpleStore.Identity.API.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        // v11: routes resolved as /api/v{version}/identity/... — see ApiVersioningExtensions.cs.
        var group = app.MapApiV1Group("identity");

        MapAuthEndpoints(group);
        MapPasskeyEndpoints(group);
        MapProfileEndpoints(group);
        MapAdminUserEndpoints(group);

        return app;
    }

    private static void MapAuthEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/register", async (RegisterRequest request, IIdentityService service, CancellationToken ct) =>
        {
            var response = await service.RegisterAsync(request, ct);
            return response is null ? Results.BadRequest(new { error = "Could not register user." }) : Results.Ok(response);
        });

        group.MapPost("/login", async (LoginRequest request, IIdentityService service, CancellationToken ct) =>
        {
            var response = await service.LoginAsync(request, ct);
            return response is null ? Results.Unauthorized() : Results.Ok(response);
        });

        group.MapPost("/refresh", async (RefreshRequest request, IIdentityService service, CancellationToken ct) =>
        {
            var response = await service.RefreshAsync(request.RefreshToken, ct);
            return response is null ? Results.Unauthorized() : Results.Ok(response);
        });

        group.MapPost("/logout", async (RefreshRequest request, IIdentityService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(request.RefreshToken, ct);
            return Results.NoContent();
        });
    }

    private static void MapPasskeyEndpoints(RouteGroupBuilder group)
    {
        var passkey = group.MapGroup("/passkey");

        passkey.MapPost("/assertion-options", async (SignInManager<ApplicationUser> signInManager) =>
        {
            var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user: null);
            return Results.Content(optionsJson, "application/json");
        });

        passkey.MapPost("/assertion", async (
            PasskeyAssertionRequest request,
            SignInManager<ApplicationUser> signInManager,
            IIdentityService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CredentialJson))
                return Results.BadRequest(new { error = "Missing credential." });

            var assertion = await signInManager.PerformPasskeyAssertionAsync(request.CredentialJson);
            if (!assertion.Succeeded || assertion.User is null)
                return Results.BadRequest(new { error = assertion.Failure?.Message ?? "Passkey assertion failed." });

            var response = await service.IssueTokensAsync(assertion.User.Id, ct);
            return response is null ? Results.Unauthorized() : Results.Ok(response);
        });

        passkey.MapPost("/creation-options", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.Unauthorized();

            var userName = user.UserName ?? user.Email ?? "User";
            var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new()
            {
                Id = user.Id,
                Name = userName,
                DisplayName = user.FullName.Length > 0 ? user.FullName : userName
            });
            return Results.Content(optionsJson, "application/json");
        }).RequireAuthorization();

        passkey.MapPost("/attestation", async (
            PasskeyAttestationRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.CredentialJson))
                return Results.BadRequest(new { error = "Missing credential." });

            var attestation = await signInManager.PerformPasskeyAttestationAsync(request.CredentialJson);
            if (!attestation.Succeeded || attestation.Passkey is null)
                return Results.BadRequest(new { error = attestation.Failure?.Message ?? "Attestation failed." });

            var passkey = attestation.Passkey;
            if (!string.IsNullOrWhiteSpace(request.Name))
                passkey.Name = request.Name.Trim();

            var add = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
            return add.Succeeded
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { error = string.Join("; ", add.Errors.Select(e => e.Description)) });
        }).RequireAuthorization();

        // GET /api/identity/passkeys
        group.MapGet("/passkeys", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.Unauthorized();

            var passkeys = await userManager.GetPasskeysAsync(user);
            var dtos = passkeys.Select(p => new SimpleStore.Identity.API.Client.UserPasskeyInfo
            {
                CredentialId = Convert.ToBase64String(p.CredentialId),
                Name = p.Name,
                CreatedAt = p.CreatedAt
            }).ToList();
            return Results.Ok(dtos);
        }).RequireAuthorization();

        group.MapDelete("/passkeys/{credentialIdBase64}", async (
            string credentialIdBase64,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.Unauthorized();

            var normalized = NormalizeBase64(credentialIdBase64);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(normalized); }
            catch (FormatException) { return Results.BadRequest(new { error = "Invalid credential id." }); }

            var result = await userManager.RemovePasskeyAsync(user, bytes);
            return result.Succeeded ? Results.NoContent() : Results.BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });
        }).RequireAuthorization();
    }

    private static void MapProfileEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            IIdentityService service,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var info = await service.GetUserInfoAsync(userId, ct);
            return info is null ? Results.NotFound() : Results.Ok(info);
        }).RequireAuthorization();

        group.MapPut("/me", async (
            UpdateProfileRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var userId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.NotFound();

            user.FullName = request.FullName?.Trim() ?? string.Empty;
            var update = await userManager.UpdateAsync(user);
            return update.Succeeded
                ? Results.NoContent()
                : Results.BadRequest(new { error = string.Join("; ", update.Errors.Select(e => e.Description)) });
        }).RequireAuthorization();
    }

    private static void MapAdminUserEndpoints(RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").RequireAuthorization("Admin");

        users.MapGet("", async (
            UserManager<ApplicationUser> userManager,
            int page = 1,
            int pageSize = 20,
            string? search = null,
            CancellationToken ct = default) =>
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 100) pageSize = 20;

            var query = userManager.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                query = query.Where(u =>
                    (u.Email != null && u.Email.Contains(s)) ||
                    u.FullName.Contains(s));
            }

            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(u => u.Email).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

            var summaries = new List<UserSummary>();
            foreach (var u in items)
            {
                var roles = await userManager.GetRolesAsync(u);
                summaries.Add(new UserSummary
                {
                    Id = u.Id,
                    Email = u.Email ?? string.Empty,
                    FullName = u.FullName,
                    LockoutEnd = u.LockoutEnd,
                    IsLockedOut = u.LockoutEnd is { } end && end > DateTimeOffset.UtcNow,
                    Roles = roles.ToArray()
                });
            }

            return Results.Ok(new PagedResult<UserSummary>
            {
                Items = summaries,
                Page = page,
                PageSize = pageSize,
                TotalCount = total
            });
        });

        users.MapGet("/count", async (UserManager<ApplicationUser> userManager, CancellationToken ct) =>
            Results.Ok(await userManager.Users.CountAsync(ct)));

        users.MapGet("/{id}", async (string id, IIdentityService service, CancellationToken ct) =>
        {
            var info = await service.GetUserInfoAsync(id, ct);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        users.MapPut("/{id}", async (string id, UpdateProfileRequest request, UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user is null) return Results.NotFound();

            user.FullName = request.FullName?.Trim() ?? string.Empty;
            var result = await userManager.UpdateAsync(user);
            return result.Succeeded
                ? Results.NoContent()
                : Results.BadRequest(new { error = string.Join("; ", result.Errors.Select(e => e.Description)) });
        });

        users.MapPost("/{id}/lock", async (string id, UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user is null) return Results.NotFound();

            await userManager.SetLockoutEnabledAsync(user, true);
            var result = await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
            return result.Succeeded ? Results.NoContent() : Results.BadRequest();
        });

        users.MapPost("/{id}/unlock", async (string id, UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user is null) return Results.NotFound();

            var result = await userManager.SetLockoutEndDateAsync(user, null);
            return result.Succeeded ? Results.NoContent() : Results.BadRequest();
        });
    }

    private static string NormalizeBase64(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
    }
}
