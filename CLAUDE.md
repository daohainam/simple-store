# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SimpleStore is a small e-commerce sample built on **.NET 10** and orchestrated with **.NET Aspire**. It is mid-migration from a monolith to microservices. Today:

- **Catalog** is fully extracted into a standalone minimal-WebApi service (`SimpleStore.Catalog.API`) that owns `catalogdb`. Storefront browsing is anonymous; admin write endpoints require a JWT with the `Admin` role.
- **Identity** is fully extracted into `SimpleStore.Identity.API`, which owns `identitydb` and issues JWT bearer tokens (HS256) plus refresh tokens. Web and Admin call it over HTTP for register/login/passkey/profile — they no longer reference `IdentityDbContext` in-process.
- **Order** still lives as a DbContext inside `SimpleStore.Data`, consumed in-process by `SimpleStore.Web` (storefront orders) and `SimpleStore.Admin` (orders admin). `orderdb` is the only database Web/Admin still talk to directly.

Cross-service auth is **JWT-bearer (HS256)**. The shared `Jwt:Issuer` / `Jwt:Audience` / `Jwt:Key` (`Jwt__*` env vars) are propagated to every service by the AppHost so any service can validate any token. Web and Admin store JWTs **server-side** in `IDistributedCache` keyed by an opaque HttpOnly `ss_session` cookie — the browser never holds the JWT itself (BFF pattern). For outbound cross-service HTTP, a `BearerTokenHandler` `DelegatingHandler` stamps `Authorization: Bearer` and transparently refreshes expired access tokens.

There are no test projects.

## Common commands

Run everything (Aspire orchestrates Postgres + PgWeb + Identity API + Catalog API + Web + Admin):

```pwsh
dotnet run --project src/SimpleStore.AppHost
```

One-time secret setup (AppHost user-secrets — `jwt-key` must be a base64-encoded byte array, 32+ bytes):

```pwsh
dotnet user-secrets set Parameters:jwt-key       "<base64 of 32 random bytes>" --project src/SimpleStore.AppHost
dotnet user-secrets set Parameters:jwt-issuer    "simple-store"                --project src/SimpleStore.AppHost
dotnet user-secrets set Parameters:jwt-audience  "simple-store"                --project src/SimpleStore.AppHost
```

Build the solution:

```pwsh
dotnet build SimpleStore.slnx
```

Run a single project directly (requires the relevant connection strings / service URIs / `Jwt__*` in user-secrets/env — normally Aspire injects them):

```pwsh
dotnet run --project src/SimpleStore.Identity.API
dotnet run --project src/SimpleStore.Catalog.API
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

EF Core migrations — one DbContext per database, each owned by a different project. The `--context` and `--output-dir` flags must always be specified so migrations land in the right folder:

```pwsh
# Catalog (lives in SimpleStore.Catalog.API)
dotnet ef migrations add <Name> --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext --output-dir Migrations
dotnet ef database update       --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext

# Identity (lives in SimpleStore.Identity.API)
dotnet ef migrations add <Name> --project src/SimpleStore.Identity.API --startup-project src/SimpleStore.Identity.API --context IdentityDbContext --output-dir Migrations
dotnet ef database update       --project src/SimpleStore.Identity.API --startup-project src/SimpleStore.Identity.API --context IdentityDbContext

# Orders (lives in SimpleStore.Data, hosted by SimpleStore.Web)
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext --output-dir Migrations/Order
dotnet ef database update       --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext
```

## Architecture

Aspire AppHost ([src/SimpleStore.AppHost/AppHost.cs](src/SimpleStore.AppHost/AppHost.cs)) is the entry point. It provisions a single `postgres` resource with PgWeb and three logical databases (`catalogdb`, `orderdb`, `identitydb`); each microservice is the only resource that touches its own DB. The AppHost also defines three parameters — `jwt-key` (secret), `jwt-issuer`, `jwt-audience` — and propagates them as `Jwt__Key` / `Jwt__Issuer` / `Jwt__Audience` env vars to every service that issues or validates JWTs. Web and Admin reference both the `catalog` and `identity` project resources (HTTP) plus `orderdb` directly.

Projects:

- **SimpleStore.Identity.API** — Minimal WebApi (`Microsoft.NET.Sdk.Web`). Owns `identitydb` end-to-end: `IdentityDbContext` + `ApplicationUser` (with `FullName`) + `RefreshToken` entity, `IIdentityService`/`IdentityService`, `IJwtTokenService`/`JwtTokenService` (HS256), `IRefreshTokenService`/`RefreshTokenService` (rotate-on-use, SHA-256 hashed), and minimal-API endpoints in `Endpoints/IdentityEndpoints.cs` under `/api/identity`. Migrates and seeds on startup (`IdentitySeeder`) — creates roles `Admin` + `Customer` and two users: `admin@simplestore.local`/`Admin123!` (Admin) and `demo@simplestore.local`/`Demo123!` (Customer). OpenAPI surface in development at `/openapi/v1.json`. Identity schema v3 is enabled so the passkey table is included. The service validates its own tokens (for `/me`, `/passkeys`, `/users` admin endpoints) using the same `Jwt:*` config it issues with.
  - Anonymous: `POST /register`, `POST /login`, `POST /refresh`, `POST /logout`, `POST /passkey/assertion-options`, `POST /passkey/assertion`.
  - Authenticated: `GET/PUT /me`, `POST /passkey/creation-options`, `POST /passkey/attestation`, `GET /passkeys`, `DELETE /passkeys/{credentialIdBase64}`.
  - Admin (`Admin` policy = role `Admin`): paged `GET /users`, `GET /users/count`, `GET/PUT /users/{id}`, `POST /users/{id}/lock|/unlock`.
- **SimpleStore.Identity.API.Client** — Shared class library referenced by Identity.API, Web, and Admin. Holds DTOs (`LoginRequest`, `LoginResponse`, `RegisterRequest`, `RefreshRequest`, `UserInfo`, `UpdateProfileRequest`, `UserSummary`, `UserPasskeyInfo`, `PasskeyAssertionRequest`, `PasskeyAttestationRequest`), a local `PagedResult<T>`, and the typed `IIdentityApiClient` / `IdentityApiClient`. The `AddIdentityApiClient` extension on `IHostApplicationBuilder` registers the typed HttpClient with `BaseAddress = new Uri("https+http://identity")` — Aspire service discovery + standard resilience from `ServiceDefaults` apply automatically.
- **SimpleStore.Catalog.API** — Minimal WebApi. Owns `catalogdb` end-to-end (same template as Identity.API). Endpoints under `/api/catalog`; reads are anonymous, writes require `RequireAuthorization("Admin")`. Validates tokens issued by Identity.API using the shared `Jwt:*` config.
  - Anonymous reads: paged `GET /products` (`?page=1&pageSize=20&categoryId={int?}&search={string?}`, `pageSize` clamped to 100), `GET /products/{id}`, `GET /products/count`, paged `GET /categories`, `GET /categories/{id}`, `GET /categories/count`.
  - Admin writes: `POST/PUT/DELETE /products/{id}` and `POST/PUT/DELETE /categories/{id}`. `DELETE /categories/{id}` returns `409 Conflict` when the category still has products.
- **SimpleStore.Catalog.API.Client** — Same template as Identity.API.Client: DTOs (`ProductDto`, `CategoryDto` with flat `CategoryName` / `ProductCount`), `PagedResult<T>`, typed `ICatalogApiClient` / `CatalogApiClient`, `AddCatalogApiClient` extension with `BaseAddress = new Uri("https+http://catalog")`.
- **SimpleStore.Web** — Customer storefront. ASP.NET Core MVC + Razor Pages. Default route is `Catalog/Index`. `CatalogController` and `CartService` go through `ICatalogApiClient`; the Razor Pages under `Areas/Identity/Pages/Account/**` go through `IIdentityApiClient`. JWT bearer is the only auth scheme — `OnMessageReceived` reads the token from `ITokenStore` (server-side cache keyed by `ss_session`) and auto-refreshes it. Outbound calls to Catalog and Identity ride `BearerTokenHandler` for `Authorization: Bearer` injection. Session-based cart (30-min idle) still works because the cart is session-id-keyed, not user-id-keyed. On startup, Web runs `MigrateAsync()` only for `OrderDbContext`; Identity and Catalog migrate themselves inside their own services.
- **SimpleStore.Admin** — Blazor Server admin dashboard. Authentication is JWT bearer with the same server-side cookie/cache trick used by Web (`Services/Auth/ITokenStore`, `CircuitTokenStore` caches the token at circuit start because `IHttpContextAccessor.HttpContext` isn't reliable during interactive Blazor operations). Login/Logout are Razor Pages at `/Account/Login` and `/Account/Logout` (not Blazor — they need a live `HttpContext` to set/clear the cookie). All Blazor pages are gated with `[Authorize(Roles = "Admin")]` and `<AuthorizeRouteView>`. `Pages/Home.razor` calls `IIdentityApiClient.GetUserCountAsync()` + `ICatalogApiClient` counts + direct `OrderDb` reads. `Pages/Customers.razor` goes entirely through `IIdentityApiClient` (no `IdentityDbContext` anywhere). `Pages/Orders.razor` reads `OrderDb` directly and bulk-fetches customer names from Identity.API.
- **SimpleStore.Data** — EF Core 10 data layer for the still-in-process Order context only:
  - `OrderDbContext` → `Order`, `OrderItem` → database `orderdb`, migrations in `Migrations/Order/`. `OrderItem.ProductName` is denormalized at order-creation time so order views never call the Catalog API.
  - Has its own `IDesignTimeDbContextFactory` for `dotnet ef`. Npgsql provider.
- **SimpleStore.ServiceDefaults** — Standard Aspire shared library: OpenTelemetry (OTLP), service discovery, HTTP resilience, default health endpoints. All four services (`Identity.API`, `Catalog.API`, `Web`, `Admin`) call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`. Distributed traces propagate end-to-end across the Web/Admin → Identity/Catalog HTTP boundary automatically.

There are no cross-database foreign keys. `Order.UserId` is a soft reference to `AspNetUsers.Id` in `identitydb`; `OrderItem.ProductId` is a soft reference to `Products.Id` in `catalogdb`. Cross-DB joins are done in application code (load + dictionary lookup) and never at the SQL level.

## Conventions

- Solution file is the new `.slnx` format (`SimpleStore.slnx`), not `.sln`.
- AppHost uses the top-level `AppHost.cs` file (no `Program.cs`).
- When a new microservice is extracted, its DbContext, models, services, migrations, and seeder all move with it; the existing client library pattern (`<Service>.API.Client` with DTOs + typed HttpClient + `Add<Service>ApiClient` extension on `IHostApplicationBuilder`) is the template for new ones.
- When adding a new shared NuGet package or service that both UIs need, update both `SimpleStore.Web` and `SimpleStore.Admin` — there is no shared application-services project.
- **Future APIs that need authentication** default to JWT bearer with the shared `Jwt:Issuer` / `Jwt:Audience` / `Jwt:Key` config (propagated from AppHost via `Jwt__*` env vars) and the named `"Admin"` policy:
  ```csharp
  builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
  {
      o.MapInboundClaims = false;
      o.TokenValidationParameters = new() { /* ValidIssuer/Audience/Key from Jwt:* */
          NameClaimType = "sub", RoleClaimType = "role", ClockSkew = TimeSpan.FromSeconds(30) };
  });
  builder.Services.AddAuthorization(o =>
      o.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin")));
  ```
  Mark write endpoints with `.RequireAuthorization("Admin")`. Set `MapInboundClaims = false` so the raw `sub`/`role`/`name` claim names survive — Web, Admin, Identity.API, and Catalog.API all agree on this.
- **Inbound auth in server-side apps (Web, Admin)**: JWT lives in `IDistributedCache` keyed by an HttpOnly `ss_session` cookie. The browser never holds the JWT. `JwtBearerOptions.Events.OnMessageReceived` reads the token from `ITokenStore`, auto-refreshing when within 30s of expiry. Login/logout in Web is a Razor Page handler that calls `IIdentityApiClient.LoginAsync` and writes the result through `ITokenStore`; Admin does the same via `/Account/Login`.
- **Outbound auth (any server-side app → any protected API)**: register a typed HttpClient with `.AddHttpMessageHandler<BearerTokenHandler>()`. `BearerTokenHandler` reads the JWT from `ITokenStore` and stamps `Authorization: Bearer`; it auto-refreshes via `IIdentityApiClient.RefreshAsync` when the access token is near-expiry.
- **Blazor Server caveat**: `IHttpContextAccessor.HttpContext` is only reliable during the initial HTTP request that establishes the SignalR circuit. Admin's `CircuitTokenStore` wraps `DistributedCacheTokenStore` and caches the token on the scoped instance so interactive button clicks can still fetch the JWT without a live `HttpContext`.
