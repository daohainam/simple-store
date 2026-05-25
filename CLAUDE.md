# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SimpleStore is a small e-commerce sample built on **.NET 10** and orchestrated with **.NET Aspire**. It uses a **database-per-service** pattern as the first step of a microservices migration: three logical PostgreSQL databases (`catalogdb`, `orderdb`, `identitydb`) on a single Postgres resource back both a customer storefront (MVC + Razor Pages) and an admin dashboard (Blazor Server). There are no test projects.

## Common commands

Run everything (Aspire orchestrates Postgres + PgAdmin + both web projects):

```pwsh
dotnet run --project src/SimpleStore.AppHost
```

Build the solution:

```pwsh
dotnet build SimpleStore.slnx
```

Run a single project directly (requires `catalogdb` / `orderdb` / `identitydb` connection strings in user-secrets/env — normally Aspire injects them):

```pwsh
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

EF Core migrations — one DbContext per database. The `--context` and `--output-dir` flags must always be specified so migrations land in the right folder:

```pwsh
# Catalog
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context CatalogDbContext  --output-dir Migrations/Catalog
dotnet ef database update       --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context CatalogDbContext

# Orders
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext    --output-dir Migrations/Order
dotnet ef database update       --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext

# Identity
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context IdentityDbContext --output-dir Migrations/Identity
dotnet ef database update       --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context IdentityDbContext
```

## Architecture

Aspire AppHost ([src/SimpleStore.AppHost/AppHost.cs](src/SimpleStore.AppHost/AppHost.cs)) is the entry point. It provisions a single `postgres` resource with PgWeb and exposes three logical databases on it — `catalogdb`, `orderdb`, `identitydb` — wiring all three into both `SimpleStore.Web` and `SimpleStore.Admin`. Each app resolves them at runtime via three `builder.AddNpgsqlDbContext<…>(…)` calls — one per bounded context.

Projects:

- **SimpleStore.Web** — Customer storefront. ASP.NET Core MVC + Razor Pages. Default route is `Catalog/Index`. Uses ASP.NET Core Identity (`ApplicationUser`, password rules relaxed) backed by `IdentityDbContext`, session-based cart (30-min idle), and scoped services `ICartService` / `ICatalogService` / `IOrderService` in `SimpleStore.Web/Services`. On startup, Web runs `MigrateAsync()` for all three contexts and seeds the catalog and a demo Identity user — Web is the only project that seeds.
- **SimpleStore.Admin** — Blazor Server admin dashboard (`AddRazorComponents().AddInteractiveServerComponents()`). Injects whichever contexts a page needs (e.g. `Customers.razor` joins `IdentityDb.Users` with `OrderDb.Orders` in-memory).
- **SimpleStore.Data** — EF Core 10 data layer. Three DbContexts:
  - `CatalogDbContext` → `Product`, `Category` → database `catalogdb`, migrations in `Migrations/Catalog/`.
  - `OrderDbContext` → `Order`, `OrderItem` → database `orderdb`, migrations in `Migrations/Order/`. `OrderItem.ProductName` is denormalized at order-creation time so order views never query the catalog DB.
  - `IdentityDbContext : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext<ApplicationUser>` → database `identitydb`, migrations in `Migrations/Identity/`. Identity schema v3 is enabled (passkey table).
  - Npgsql provider. Each context has its own `IDesignTimeDbContextFactory` for `dotnet ef`.
- **SimpleStore.ServiceDefaults** — Standard Aspire shared library: OpenTelemetry (OTLP), service discovery, HTTP resilience, default health endpoints. Both web apps call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`.

There are no cross-database foreign keys. `Order.UserId` is a soft reference to `AspNetUsers.Id` in `identitydb`; `OrderItem.ProductId` is a soft reference to `Products.Id` in `catalogdb`. Joins across DBs are done in application code (load + dictionary lookup), not at the SQL level.

## Conventions

- Solution file is the new `.slnx` format (`SimpleStore.slnx`), not `.sln`.
- AppHost uses the top-level `AppHost.cs` file (no `Program.cs`).
- When adding a new shared service or NuGet package, update both `SimpleStore.Web` and `SimpleStore.Admin` if it needs to be available in both UIs — there is no shared application-services project.
