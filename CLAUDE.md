# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SimpleStore is a small e-commerce sample built on **.NET 10** and orchestrated with **.NET Aspire**. It is mid-migration from a monolith to microservices. Today:

- **Catalog** is fully extracted into a standalone minimal-WebApi service (`SimpleStore.Catalog.API`) that owns `catalogdb`. The storefront and admin dashboard reach it over HTTP via Aspire service discovery.
- **Order** and **Identity** still live as DbContexts inside `SimpleStore.Data`, consumed in-process by `SimpleStore.Web` and `SimpleStore.Admin`. They use a database-per-service split (`orderdb`, `identitydb`) on the same Postgres resource.

There are no test projects.

## Common commands

Run everything (Aspire orchestrates Postgres + PgWeb + Catalog API + Web + Admin):

```pwsh
dotnet run --project src/SimpleStore.AppHost
```

Build the solution:

```pwsh
dotnet build SimpleStore.slnx
```

Run a single project directly (requires the relevant connection strings / `services__catalog__http__0` in user-secrets/env — normally Aspire injects them):

```pwsh
dotnet run --project src/SimpleStore.Catalog.API
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

EF Core migrations — one DbContext per database, each owned by a different project. The `--context` and `--output-dir` flags must always be specified so migrations land in the right folder:

```pwsh
# Catalog (lives in SimpleStore.Catalog.API)
dotnet ef migrations add <Name> --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext --output-dir Migrations
dotnet ef database update       --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext

# Orders (lives in SimpleStore.Data, hosted by SimpleStore.Web)
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext    --output-dir Migrations/Order
dotnet ef database update       --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext

# Identity (lives in SimpleStore.Data, hosted by SimpleStore.Web)
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context IdentityDbContext --output-dir Migrations/Identity
dotnet ef database update       --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context IdentityDbContext
```

## Architecture

Aspire AppHost ([src/SimpleStore.AppHost/AppHost.cs](src/SimpleStore.AppHost/AppHost.cs)) is the entry point. It provisions a single `postgres` resource with PgWeb and three logical databases (`catalogdb`, `orderdb`, `identitydb`). The Catalog microservice is the only resource that references `catalogdb`; `web` and `admin` reference the `catalog` project resource (HTTP) plus `orderdb`/`identitydb` directly.

Projects:

- **SimpleStore.Catalog.API** — Minimal WebApi (`Microsoft.NET.Sdk.Web`). Owns `catalogdb` end-to-end: `CatalogDbContext` + `Product`/`Category` entities, `ICatalogService`/`CatalogService` (internal), and minimal-API endpoint mappings in `Endpoints/CatalogEndpoints.cs` under `/api/catalog`. Migrates and seeds on startup (`CatalogSeeder`). OpenAPI surface exposed in development at `/openapi/v1.json`. Internal-only — no authentication; revisit when externalizing.
  - Read endpoints (used by Web + Admin): paged `GET /products` (`?page=1&pageSize=20&categoryId={int?}&search={string?}`, `pageSize` clamped to 100 server-side), `GET /products/{id}`, `GET /products/count`, paged `GET /categories`, `GET /categories/{id}`, `GET /categories/count`.
  - Write endpoints (used by Admin): `POST/PUT/DELETE /products/{id}` and `POST/PUT/DELETE /categories/{id}`. `DELETE /categories/{id}` returns `409 Conflict` when the category still has products.
- **SimpleStore.Catalog.API.Client** — Shared class library referenced by Catalog.API, Web, and Admin. Holds `ProductDto`, `CategoryDto` (with flat `CategoryName` / `ProductCount` so consumers never need EF navigations), `PagedResult<T>`, and the typed `ICatalogApiClient` / `CatalogApiClient` (uses `System.Net.Http.Json`). The `AddCatalogApiClient` extension on `IHostApplicationBuilder` registers the typed HttpClient with `BaseAddress = new Uri("https+http://catalog")` — Aspire service discovery + standard resilience from `ServiceDefaults` apply automatically.
- **SimpleStore.Web** — Customer storefront. ASP.NET Core MVC + Razor Pages. Default route is `Catalog/Index`. `CatalogController` and `CartService` go through `ICatalogApiClient` — no `CatalogDbContext` is registered here. Uses ASP.NET Core Identity (`ApplicationUser`, password rules relaxed) backed by `IdentityDbContext`, session-based cart (30-min idle), and scoped services `ICartService` / `IOrderService` in `SimpleStore.Web/Services`. On startup, Web runs `MigrateAsync()` for the Order + Identity contexts and seeds a demo Identity user — Catalog migrates itself inside its own service.
- **SimpleStore.Admin** — Blazor Server admin dashboard (`AddRazorComponents().AddInteractiveServerComponents()`). `Pages/Products.razor` and `Pages/Categories.razor` do every read and write through `ICatalogApiClient` — there is no EF Core path to the catalog from Admin. `Pages/Home.razor` uses the catalog `/count` endpoints plus direct `IdentityDb`/`OrderDb` queries; `Pages/Customers.razor` and `Pages/Orders.razor` still query `IdentityDb`/`OrderDb` in-process.
- **SimpleStore.Data** — EF Core 10 data layer for the two still-in-process contexts:
  - `OrderDbContext` → `Order`, `OrderItem` → database `orderdb`, migrations in `Migrations/Order/`. `OrderItem.ProductName` is denormalized at order-creation time so order views never call the Catalog API.
  - `IdentityDbContext : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext<ApplicationUser>` → database `identitydb`, migrations in `Migrations/Identity/`. Identity schema v3 is enabled (passkey table).
  - Each context has its own `IDesignTimeDbContextFactory` for `dotnet ef`. Npgsql provider.
- **SimpleStore.ServiceDefaults** — Standard Aspire shared library: OpenTelemetry (OTLP), service discovery, HTTP resilience, default health endpoints. All three services (`Catalog.API`, `Web`, `Admin`) call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`. Distributed traces propagate end-to-end across the Web/Admin → Catalog HTTP boundary automatically.

There are no cross-database foreign keys. `Order.UserId` is a soft reference to `AspNetUsers.Id` in `identitydb`; `OrderItem.ProductId` is a soft reference to `Products.Id` in `catalogdb`. Cross-DB joins are done in application code (load + dictionary lookup) and never at the SQL level.

## Conventions

- Solution file is the new `.slnx` format (`SimpleStore.slnx`), not `.sln`.
- AppHost uses the top-level `AppHost.cs` file (no `Program.cs`).
- When a new microservice is extracted, its DbContext, models, services, migrations, and seeder all move with it; the existing client library pattern (`<Service>.API.Client` with DTOs + typed HttpClient + `Add<Service>ApiClient` extension on `IHostApplicationBuilder`) is the template for new ones.
- When adding a new shared NuGet package or service that both UIs need, update both `SimpleStore.Web` and `SimpleStore.Admin` — there is no shared application-services project.
