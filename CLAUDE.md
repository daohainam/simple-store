# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SimpleStore is a small e-commerce sample built on **.NET 10** and orchestrated with **.NET Aspire**. A single PostgreSQL database (`storedb`) backs both a customer storefront (MVC + Razor Pages) and an admin dashboard (Blazor Server). There are no test projects.

## Common commands

Run everything (Aspire orchestrates Postgres + PgAdmin + both web projects):

```pwsh
dotnet run --project src/SimpleStore.AppHost
```

Build the solution:

```pwsh
dotnet build SimpleStore.slnx
```

Run a single project directly (requires a `storedb` connection string in user-secrets/env — normally Aspire injects it):

```pwsh
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

EF Core migrations (DbContext lives in SimpleStore.Data; pick either web project as the startup):

```pwsh
dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web
dotnet ef database update     --project src/SimpleStore.Data --startup-project src/SimpleStore.Web
```

## Architecture

Aspire AppHost ([src/SimpleStore.AppHost/AppHost.cs](src/SimpleStore.AppHost/AppHost.cs)) is the entry point. It provisions a `postgres` resource with PgAdmin, exposes a `storedb` database, and wires both `SimpleStore.Web` and `SimpleStore.Admin` to it via `WithReference(storeDb).WaitFor(storeDb)`. The connection string name `storedb` is what both apps resolve at runtime via `builder.AddNpgsqlDbContext<StoreDbContext>("storedb")`.

Projects:

- **SimpleStore.Web** — Customer storefront. ASP.NET Core MVC + Razor Pages. Default route is `Catalog/Index`. Uses ASP.NET Core Identity (`ApplicationUser`, password rules relaxed), session-based cart (30-min idle), and scoped services `ICartService` / `ICatalogService` / `IOrderService` in `SimpleStore.Web/Services`. Calls `DbSeeder.SeedAsync` on startup — Web is the only project that seeds.
- **SimpleStore.Admin** — Blazor Server admin dashboard (`AddRazorComponents().AddInteractiveServerComponents()`). Shares the same `storedb` and `StoreDbContext`.
- **SimpleStore.Data** — EF Core 10 data layer. `StoreDbContext : IdentityDbContext<ApplicationUser>` with entities `Product`, `Category`, `Order`, `OrderItem`. Npgsql provider. Single migration `20260508170403_InitialCreate` configures `decimal(18,2)` on money fields. `DbSeeder` populates initial catalog data.
- **SimpleStore.ServiceDefaults** — Standard Aspire shared library: OpenTelemetry (OTLP), service discovery, HTTP resilience, default health endpoints. Both web apps call `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()`.

Identity tables live in the same `StoreDbContext`, so the same database hosts both domain and auth data. Both apps must stay in sync on the Identity configuration when changes are made.

## Conventions

- Solution file is the new `.slnx` format (`SimpleStore.slnx`), not `.sln`.
- AppHost uses the top-level `AppHost.cs` file (no `Program.cs`).
- When adding a new shared service or NuGet package, update both `SimpleStore.Web` and `SimpleStore.Admin` if it needs to be available in both UIs — there is no shared application-services project.
