# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SimpleStore is a small e-commerce sample built on **.NET 10** and orchestrated with **.NET Aspire**. The monolith → microservices migration is now complete:

- **Catalog** is a standalone minimal-WebApi service (`SimpleStore.Catalog.API`) that owns `catalogdb` (Postgres). Storefront browsing is anonymous; admin write endpoints require a JWT with the `Admin` role.
- **Identity** (`SimpleStore.Identity.API`) owns `identitydb` (Postgres) and issues JWT bearer tokens (HS256) plus refresh tokens. Web and Admin call it over HTTP for register/login/passkey/profile.
- **Order** (`SimpleStore.Order.API`) owns `orderdb` (Postgres). Storefront endpoints (`/api/order/orders`) require the caller's JWT; admin endpoints (`/api/order/admin/...`) require the `Admin` role.
- **Cart** (`SimpleStore.Cart.API`) is backed by **Redis** (`cart-redis`). It is the first non-DB-backed microservice. Anonymous browsers identify a cart with the opaque `ss_cart` HttpOnly cookie issued by `SimpleStore.Web`, which it passes to Cart.API as the `X-Cart-Id` header; authenticated callers are keyed by the JWT `sub` claim. A `/api/cart/merge` endpoint (auth required) folds the anonymous cart into the user's cart on login.

**Event-driven flows (v6)** ride a **RabbitMQ** broker via **MassTransit**. Shared event records live in `SimpleStore.Contracts` (no other dependencies). Two flows are wired today:
- **`OrderSubmittedEvent`** — Order.API publishes it after checkout; Catalog.API consumes it to decrement `Product.Stock`.
- **`ProductUpdatedEvent`** — Catalog.API publishes it after an admin product edit; Cart.API consumes it to refresh the denormalized `ProductName` / `UnitPrice` / `ImageUrl` on every cart line referencing that product.

Web and Admin no longer host any DbContext — they only talk HTTP. Cross-service auth is **JWT-bearer (HS256)**. The shared `Jwt:Issuer` / `Jwt:Audience` / `Jwt:Key` (`Jwt__*` env vars) are propagated to every service by the AppHost so any service can validate any token. Web and Admin store JWTs **server-side** in `IDistributedCache` keyed by an opaque HttpOnly `ss_session` cookie — the browser never holds the JWT itself (BFF pattern). For outbound cross-service HTTP, a `BearerTokenHandler` `DelegatingHandler` stamps `Authorization: Bearer` and transparently refreshes expired access tokens.

There are no test projects.

### Migration changelog

- **v6** — Added RabbitMQ broker (Aspire `rabbitmq` resource, Management plugin) and MassTransit. New `SimpleStore.Contracts` class library holds shared event records. Catalog.API consumes `OrderSubmittedEvent` (decrements `Product.Stock`); Cart.API consumes `ProductUpdatedEvent` (refreshes denormalized line items). Order.API and Catalog.API publish through the **MassTransit EF Core transactional outbox** (the event row commits in the same DB transaction as the entity write); Cart.API has no DbContext and relies on at-least-once delivery + an idempotent consumer.
- **v5** — Introduced `SimpleStore.Gateway` (YARP) as the single entry point for Web and Admin; backend services moved off direct service references.
- **v4** — Extracted `SimpleStore.Order.API` (Postgres, `orderdb`) and `SimpleStore.Cart.API` (Redis, `cart-redis`); deleted `SimpleStore.Data`. Web and Admin are now pure HTTP clients.
- **v3** — Extracted `SimpleStore.Identity.API`; Web/Admin stopped referencing `IdentityDbContext`.
- **v2** — Extracted `SimpleStore.Catalog.API`; introduced the `<Service>.API.Client` library template.
- **v1** — Separate database per service.
- **v0** — Initial version.

## Common commands

Run everything (Aspire orchestrates Postgres + PgWeb + Redis + RedisInsight + RabbitMQ + Management UI + Identity API + Catalog API + Order API + Cart API + Gateway + Web + Admin):

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
dotnet run --project src/SimpleStore.Order.API
dotnet run --project src/SimpleStore.Cart.API
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

EF Core migrations — one DbContext per database, each owned by its API project. The `--context` and `--output-dir` flags must always be specified so migrations land in the right folder:

```pwsh
# Catalog (lives in SimpleStore.Catalog.API)
dotnet ef migrations add <Name> --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext --output-dir Migrations
dotnet ef database update       --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext

# Identity (lives in SimpleStore.Identity.API)
dotnet ef migrations add <Name> --project src/SimpleStore.Identity.API --startup-project src/SimpleStore.Identity.API --context IdentityDbContext --output-dir Migrations
dotnet ef database update       --project src/SimpleStore.Identity.API --startup-project src/SimpleStore.Identity.API --context IdentityDbContext

# Orders (lives in SimpleStore.Order.API)
dotnet ef migrations add <Name> --project src/SimpleStore.Order.API --startup-project src/SimpleStore.Order.API --context OrderDbContext --output-dir Migrations
dotnet ef database update       --project src/SimpleStore.Order.API --startup-project src/SimpleStore.Order.API --context OrderDbContext
```

Cart.API has no DbContext — Redis schema is implicit.

## Architecture

Aspire AppHost ([src/SimpleStore.AppHost/AppHost.cs](src/SimpleStore.AppHost/AppHost.cs)) is the entry point. It provisions one `postgres` resource with PgWeb and three logical databases (`catalogdb`, `orderdb`, `identitydb`), one `cart-redis` resource with RedisInsight, and one `rabbitmq` resource with the Management plugin; each microservice is the only resource that touches its own data store, and `order` / `catalog` / `cart` reference `rabbitmq`. The AppHost also defines three parameters — `jwt-key` (secret), `jwt-issuer`, `jwt-audience` — and propagates them as `Jwt__Key` / `Jwt__Issuer` / `Jwt__Audience` env vars to every service that issues or validates JWTs. Web and Admin only reference the `gateway` (YARP reverse proxy) — they no longer reference backend services directly.

Projects:

- **SimpleStore.Identity.API** — Minimal WebApi (`Microsoft.NET.Sdk.Web`). Owns `identitydb` end-to-end: `IdentityDbContext` + `ApplicationUser` (with `FullName`) + `RefreshToken` entity, `IIdentityService`/`IdentityService`, `IJwtTokenService`/`JwtTokenService` (HS256), `IRefreshTokenService`/`RefreshTokenService` (rotate-on-use, SHA-256 hashed), and minimal-API endpoints in `Endpoints/IdentityEndpoints.cs` under `/api/identity`. Migrates and seeds on startup (`IdentitySeeder`) — creates roles `Admin` + `Customer` and two users: `admin@simplestore.local`/`Admin123!` (Admin) and `demo@simplestore.local`/`Demo123!` (Customer). OpenAPI surface in development at `/openapi/v1.json`. Identity schema v3 is enabled so the passkey table is included. The service validates its own tokens (for `/me`, `/passkeys`, `/users` admin endpoints) using the same `Jwt:*` config it issues with.
  - Anonymous: `POST /register`, `POST /login`, `POST /refresh`, `POST /logout`, `POST /passkey/assertion-options`, `POST /passkey/assertion`.
  - Authenticated: `GET/PUT /me`, `POST /passkey/creation-options`, `POST /passkey/attestation`, `GET /passkeys`, `DELETE /passkeys/{credentialIdBase64}`.
  - Admin (`Admin` policy = role `Admin`): paged `GET /users`, `GET /users/count`, `GET/PUT /users/{id}`, `POST /users/{id}/lock|/unlock`.
- **SimpleStore.Identity.API.Client** — Shared class library referenced by Identity.API, Web, and Admin. Holds DTOs (`LoginRequest`, `LoginResponse`, `RegisterRequest`, `RefreshRequest`, `UserInfo`, `UpdateProfileRequest`, `UserSummary`, `UserPasskeyInfo`, `PasskeyAssertionRequest`, `PasskeyAttestationRequest`), a local `PagedResult<T>`, and the typed `IIdentityApiClient` / `IdentityApiClient`. The `AddIdentityApiClient` extension on `IHostApplicationBuilder` registers the typed HttpClient with `BaseAddress = new Uri("https+http://identity")` — Aspire service discovery + standard resilience from `ServiceDefaults` apply automatically.
- **SimpleStore.Catalog.API** — Minimal WebApi. Owns `catalogdb` end-to-end (same template as Identity.API). Endpoints under `/api/catalog`; reads are anonymous, writes require `RequireAuthorization("Admin")`. Validates tokens issued by Identity.API using the shared `Jwt:*` config. **Publishes `ProductUpdatedEvent`** from `CatalogService.UpdateProductAsync` via the EF Core outbox (only on update — create/delete are out of scope for v6). **Consumes `OrderSubmittedEvent`** via `Consumers/OrderSubmittedConsumer`, which loads each referenced product and decrements `Product.Stock` by the ordered quantity; the EF Core inbox makes the consume idempotent (no double-decrement on redelivery). Stock is allowed to go negative — same posture as the direct write endpoints, treated as a signal to operations.
  - Anonymous reads: paged `GET /products` (`?page=1&pageSize=20&categoryId={int?}&search={string?}`, `pageSize` clamped to 100), `GET /products/{id}`, `GET /products/count`, paged `GET /categories`, `GET /categories/{id}`, `GET /categories/count`.
  - Admin writes: `POST/PUT/DELETE /products/{id}` and `POST/PUT/DELETE /categories/{id}`. `DELETE /categories/{id}` returns `409 Conflict` when the category still has products.
- **SimpleStore.Catalog.API.Client** — Same template as Identity.API.Client: DTOs (`ProductDto`, `CategoryDto` with flat `CategoryName` / `ProductCount`), `PagedResult<T>`, typed `ICatalogApiClient` / `CatalogApiClient`, `AddCatalogApiClient` extension with `BaseAddress = new Uri("https+http://catalog")`.
- **SimpleStore.Contracts** — Tiny class library with **no external references**. Holds the shared event records that ride the RabbitMQ bus: `OrderSubmittedEvent` (+ `OrderSubmittedLineItem`) and `ProductUpdatedEvent`. Referenced by `Order.API`, `Catalog.API`, and `Cart.API`. Keep types immutable (`init` setters, `IReadOnlyList<>`) and additive — every consumer must agree on the wire shape.
- **SimpleStore.Order.API** — Minimal WebApi. Owns `orderdb` end-to-end: `OrderDbContext` + `Order` + `OrderItem` (with `ProductName` denormalized at order-creation time so views never call Catalog), `IOrderService`/`OrderService`, and minimal-API endpoints in `Endpoints/OrderEndpoints.cs` under `/api/order`. Migrates on startup (`OrderSeeder` only calls `MigrateAsync` — orders are user-generated, no seed data). Validates tokens with the same `Jwt:*` config. **Publishes `OrderSubmittedEvent`** at the end of `OrderService.CreateOrderAsync` through the MassTransit EF Core outbox: the publish is wrapped in `Database.BeginTransactionAsync` so the order row and the `OutboxMessage` row commit atomically.
  - Authenticated (owner = `sub` claim): `GET /orders`, `GET /orders/{id}`, `POST /orders` (body `CreateOrderRequest { ShippingAddress, Items[] }`).
  - Admin (`Admin` policy): paged `GET /admin/orders`, `GET /admin/orders/count`, `GET /admin/orders/{id}`, `PATCH /admin/orders/{id}/status` (body `UpdateOrderStatusRequest { Status }`), `GET /admin/orders/stats` (single-trip aggregate: `TotalCount`/`PendingCount`/`CompletedCount`/`TotalRevenue` — used by the Admin dashboard), and `GET /admin/orders/counts-by-user` (bulk per-user counts dictionary — used by the Admin Customers page).
- **SimpleStore.Order.API.Client** — Same template: DTOs (`OrderDto`, `OrderItemDto`, `CreateOrderRequest`, `UpdateOrderStatusRequest`, `OrderStatsDto`), `PagedResult<T>`, typed `IOrderApiClient` / `OrderApiClient`, `AddOrderApiClient` extension with `BaseAddress = new Uri("https+http://order")`. Because the entity name `Order` collides with the second segment of the `SimpleStore.Order.API.*` namespace tree, `OrderDbContext` and `OrderService` use a `using OrderEntity = SimpleStore.Order.API.Models.Order;` alias — keep that convention if you add more code that touches the EF entity directly.
- **SimpleStore.Cart.API** — Minimal WebApi backed by Redis. Pulls in both `Aspire.StackExchange.Redis.DistributedCaching` (for `IDistributedCache`) and `Aspire.StackExchange.Redis` (for `IConnectionMultiplexer`, needed by SCAN) — both target the same `cart-redis` resource. No DbContext — `RedisCartStore` serializes a `List<CartItemDto>` to JSON under the key `cart:{ownerKey}` with 30-day sliding expiration. Endpoints in `Endpoints/CartEndpoints.cs` under `/api/cart` are mostly `.AllowAnonymous()` so anonymous shoppers work; the per-endpoint `ResolveOwner` helper picks the owner key from `User.FindFirst("sub")` (preferred) or the `X-Cart-Id` header (fallback), and returns `400 Bad Request` if neither is present. Web sends fully enriched line items (`{ProductId, ProductName, UnitPrice, ImageUrl, Quantity}`) — Cart.API does not call Catalog. The `POST /api/cart/merge` endpoint is the one exception: it requires a JWT, takes `{ AnonymousCartId }`, and folds the anonymous cart into the user's cart. **Consumes `ProductUpdatedEvent`** via `Consumers/ProductUpdatedConsumer`, which **enumerates every cart key with Redis SCAN** (`IConnectionMultiplexer.GetServer(...).KeysAsync("cart:*")`, exposed as `ICartStore.EnumerateOwnerKeysAsync`) and refreshes the denormalized `ProductName` / `UnitPrice` / `ImageUrl` on any line whose `ProductId` matches. SCAN is non-blocking on the server; the design is linear in cart count and acceptable while the cart-key count is small/medium. **No reverse index is maintained** — keeps cart writes unchanged at the cost of a fan-out scan on each product update. The consumer has no inbox (Cart has no DbContext); duplicate delivery is harmless because rewriting identical fields is idempotent.
  - Items: `GET /api/cart`, `POST /api/cart/items`, `PUT /api/cart/items/{productId}`, `DELETE /api/cart/items/{productId}`, `DELETE /api/cart`, `GET /api/cart/count`, `GET /api/cart/total`.
  - Merge: `POST /api/cart/merge` (auth).
- **SimpleStore.Cart.API.Client** — Same template: DTOs (`CartDto`, `CartItemDto`, `AddCartItemRequest`, `UpdateCartItemRequest`, `MergeCartRequest`), typed `ICartApiClient` / `CartApiClient`, `AddCartApiClient` extension with `BaseAddress = new Uri("https+http://cart")`. The client never sets `X-Cart-Id` itself — that is the consumer's `CartIdHandler` `DelegatingHandler` (see SimpleStore.Web).
- **SimpleStore.Web** — Customer storefront. ASP.NET Core MVC + Razor Pages. Default route is `Catalog/Index`. **No DbContext**. `CatalogController` and `CartController` go through `ICatalogApiClient` / `ICartApiClient`; `OrdersController` goes through `IOrderApiClient`; the Razor Pages under `Areas/Identity/Pages/Account/**` go through `IIdentityApiClient`. JWT bearer is the only auth scheme — `OnMessageReceived` reads the token from `ITokenStore` (server-side cache keyed by `ss_session`) and auto-refreshes it. Outbound calls to Catalog/Identity/Order ride `BearerTokenHandler`; outbound calls to Cart ride `BearerTokenHandler` + `CartIdHandler`. Anonymous cart support: `Services/Cart/CartCookieManager` issues the `ss_cart` HttpOnly cookie (GUID, 30-day) on first cart write for anonymous users; `Services/Cart/CartIdHandler` stamps `X-Cart-Id` on outbound Cart.API calls when the cookie is present; `Services/Cart/CartMergeMiddleware` runs after `UseAuthentication` and, on the first authenticated request after login, calls `ICartApiClient.MergeAsync(anonCartId)` and clears the cookie.
- **SimpleStore.Admin** — Blazor Server admin dashboard. **No DbContext** — `Pages/Orders.razor` now uses `IOrderApiClient.GetOrdersAsync` / `UpdateOrderStatusAsync` (was direct EF), and `Pages/Home.razor` uses `IOrderApiClient.GetStatsAsync` (was four direct count queries). The customer-name lookup in Orders.razor still bulk-fetches from `IIdentityApiClient.GetUsersAsync` and dictionary-joins in memory. Authentication is JWT bearer with the same server-side cookie/cache trick used by Web (`Services/Auth/ITokenStore`, `CircuitTokenStore` caches the token at circuit start because `IHttpContextAccessor.HttpContext` isn't reliable during interactive Blazor operations). Login/Logout are Razor Pages at `/Account/Login` and `/Account/Logout`. All Blazor pages are gated with `[Authorize(Roles = "Admin")]` and the `FallbackPolicy = Admin` enforces it. Admin does **not** reference `SimpleStore.Cart.API.Client` — there is no cart UI in admin.
- **SimpleStore.ServiceDefaults** — Standard Aspire shared library: OpenTelemetry (OTLP), service discovery, HTTP resilience, default health endpoints. All six services (`Identity.API`, `Catalog.API`, `Order.API`, `Cart.API`, `Web`, `Admin`) call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`. Distributed traces propagate end-to-end across all service boundaries automatically.

There are no cross-database foreign keys. `Order.UserId` is a soft reference to `AspNetUsers.Id` in `identitydb`; `OrderItem.ProductId` is a soft reference to `Products.Id` in `catalogdb`. Cross-DB joins are done in application code (load + dictionary lookup) and never at the SQL level.

## Conventions

- Solution file is the new `.slnx` format (`SimpleStore.slnx`), not `.sln`.
- AppHost uses the top-level `AppHost.cs` file (no `Program.cs`).
- When a new microservice is extracted, its DbContext (if any), models, services, migrations, and seeder all move with it; the existing client library pattern (`<Service>.API.Client` with DTOs + typed HttpClient + `Add<Service>ApiClient` extension on `IHostApplicationBuilder`) is the template for new ones.
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
  Mark write endpoints with `.RequireAuthorization("Admin")`. Set `MapInboundClaims = false` so the raw `sub`/`role`/`name` claim names survive — Web, Admin, and every API agree on this.
- **Inbound auth in server-side apps (Web, Admin)**: JWT lives in `IDistributedCache` keyed by an HttpOnly `ss_session` cookie. The browser never holds the JWT. `JwtBearerOptions.Events.OnMessageReceived` reads the token from `ITokenStore`, auto-refreshing when within 30s of expiry. Login/logout in Web is a Razor Page handler that calls `IIdentityApiClient.LoginAsync` and writes the result through `ITokenStore`; Admin does the same via `/Account/Login`.
- **Outbound auth (any server-side app → any protected API)**: register a typed HttpClient with `.AddHttpMessageHandler<BearerTokenHandler>()`. `BearerTokenHandler` reads the JWT from `ITokenStore` and stamps `Authorization: Bearer`; it auto-refreshes via `IIdentityApiClient.RefreshAsync` when the access token is near-expiry.
- **Anonymous-friendly APIs (Cart.API)**: when an API intentionally allows anonymous access, endpoints use `.AllowAnonymous()` and resolve identity per request (`User.FindFirst("sub")` first, then a fallback header). The consumer wires both `BearerTokenHandler` and a per-API "id handler" (`CartIdHandler` for Cart) onto the typed HttpClient: `builder.AddCartApiClient().AddHttpMessageHandler<BearerTokenHandler>().AddHttpMessageHandler<CartIdHandler>()`. Cart-specifically, the `ss_cart` cookie lives only in `SimpleStore.Web` and is cleared after merge on first authenticated request via `CartMergeMiddleware`.
- **Blazor Server caveat**: `IHttpContextAccessor.HttpContext` is only reliable during the initial HTTP request that establishes the SignalR circuit. Admin's `CircuitTokenStore` wraps `DistributedCacheTokenStore` and caches the token on the scoped instance so interactive button clicks can still fetch the JWT without a live `HttpContext`.
- **Event contracts** live in `SimpleStore.Contracts` as immutable records with no external dependencies. Any service that publishes or consumes references this project. New events are additive — never rename or repurpose existing fields, because every subscriber needs to agree on the wire shape.
- **Event publishing** (Order, Catalog): use `IPublishEndpoint.Publish` inside a `BeginTransactionAsync` block so the entity insert and the `OutboxMessage` row commit atomically. MassTransit's hosted `BusOutbox` delivers from the table asynchronously — crashes between save and deliver are recovered on the next start. Don't publish from anywhere that can't carry the EF Core outbox interceptor (e.g. fire-and-forget background services).
- **Event consuming**: consumers live under `Consumers/<EventName>Consumer.cs` and implement `IConsumer<T>`. Services with a DbContext (Catalog) get an EF Core **inbox** via `AddEntityFrameworkOutbox<TDbContext>` so consumes are exactly-once; services without one (Cart) **must** write idempotent handlers because duplicate delivery is possible. MassTransit's `ConfigureEndpoints(ctx)` is the only endpoint config needed — it auto-binds queues per consumer.
- **Cart fan-out**: `RedisCartStore` is keyed only by `cart:{ownerKey}` with no secondary index. Consumers that need to update many carts use **Redis SCAN via `IConnectionMultiplexer.GetServer(...).KeysAsync("cart:*")`** (exposed through `ICartStore.EnumerateOwnerKeysAsync`), **not** a maintained reverse index. SCAN is non-blocking on the Redis server and acceptable while cart-key count is small/medium; revisit only if production cart-key counts grow into the high tens of thousands.
