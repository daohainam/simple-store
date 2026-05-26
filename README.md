# SimpleStore

A sample e-commerce application built with **.NET 10**, **.NET Aspire**, **Entity Framework Core**, **PostgreSQL**, and **Redis**. It is a microservices reference architecture: a customer storefront (`SimpleStore.Web`) and a Blazor Server admin dashboard (`SimpleStore.Admin`) act as BFFs in front of four small services (Identity, Catalog, Order, Cart), all orchestrated by a single Aspire AppHost.

---

## Features

### Storefront (`SimpleStore.Web`)
- Product catalog browsing by category (calls `SimpleStore.Catalog.API`)
- Redis-backed shopping cart (calls `SimpleStore.Cart.API`) — anonymous and authenticated carts both supported, merged automatically on login
- Order placement and order history (calls `SimpleStore.Order.API`)
- JWT-bearer authentication via `SimpleStore.Identity.API` (HS256, with refresh tokens and passkeys/WebAuthn)
- Demo accounts seeded on first run:
  - `admin@simplestore.local` / `Admin123!` (Admin)
  - `demo@simplestore.local` / `Demo123!` (Customer)

### Admin Dashboard (`SimpleStore.Admin`)
- Blazor Server interactive UI, gated to the `Admin` role
- Dashboard tiles for customer / catalog / order counts and a sales snapshot
- Manage **Products**, **Categories**, **Orders** (status updates), and **Customers** — every read/write goes through a typed HTTP client

---

## Screenshots

### Frontend

![SimpleStore Frontend](img/frontend.png)

### Backend

![SimpleStore Backend](img/backend.png)

---

## Project Structure

```
SimpleStore.slnx
src/
├── SimpleStore.AppHost              # .NET Aspire orchestration entry point
├── SimpleStore.ServiceDefaults      # Shared Aspire defaults (OTel, health, resilience)
├── SimpleStore.Identity.API         # Identity microservice (owns identitydb)
├── SimpleStore.Identity.API.Client  # DTOs + typed HttpClient for Identity
├── SimpleStore.Catalog.API          # Catalog microservice (owns catalogdb)
├── SimpleStore.Catalog.API.Client   # DTOs + typed HttpClient for Catalog
├── SimpleStore.Order.API            # Order microservice (owns orderdb)
├── SimpleStore.Order.API.Client     # DTOs + typed HttpClient for Order
├── SimpleStore.Cart.API             # Cart microservice (Redis-backed)
├── SimpleStore.Cart.API.Client      # DTOs + typed HttpClient for Cart
├── SimpleStore.Web                  # Customer storefront (ASP.NET Core MVC + Razor Pages)
└── SimpleStore.Admin                # Admin dashboard (Blazor Server)
```

### Services

| Service                  | Storage           | Auth                                                  |
|--------------------------|-------------------|-------------------------------------------------------|
| `SimpleStore.Identity.API` | Postgres `identitydb` | Issues JWTs; validates its own for `/me`, `/users`    |
| `SimpleStore.Catalog.API`  | Postgres `catalogdb`  | Anonymous reads; `Admin`-only writes                  |
| `SimpleStore.Order.API`    | Postgres `orderdb`    | Auth required (owner = `sub`); `Admin`-only on admin endpoints |
| `SimpleStore.Cart.API`     | Redis `cart-redis`    | Anonymous endpoints via `X-Cart-Id` header; merge requires auth |

There are no cross-database foreign keys — `Order.UserId` and `OrderItem.ProductId` are soft references. Cross-service joins happen in application code (bulk fetch + dictionary lookup).

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [.NET Aspire workload](https://learn.microsoft.com/dotnet/aspire/fundamentals/setup-tooling)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (used by Aspire to run PostgreSQL and Redis)

Install the Aspire workload if needed:

```pwsh
dotnet workload install aspire
```

---

## Getting Started

### One-time secret setup

The AppHost expects three user-secret parameters. `jwt-key` must be a base64-encoded 32-byte (or longer) array:

```pwsh
dotnet user-secrets set Parameters:jwt-key       "<base64 of 32 random bytes>" --project src/SimpleStore.AppHost
dotnet user-secrets set Parameters:jwt-issuer    "simple-store"                --project src/SimpleStore.AppHost
dotnet user-secrets set Parameters:jwt-audience  "simple-store"                --project src/SimpleStore.AppHost
```

### Run with Aspire (recommended)

Aspire starts PostgreSQL (+ PgWeb), Redis (+ RedisInsight), all four APIs, Web, and Admin:

```pwsh
dotnet run --project src/SimpleStore.AppHost
```

Open the Aspire dashboard URL printed in the console to see service endpoints, logs, and distributed traces.

### Run a single project

Each project requires the relevant connection strings / service URIs / `Jwt__*` env vars (normally injected by Aspire):

```pwsh
dotnet run --project src/SimpleStore.Identity.API
dotnet run --project src/SimpleStore.Catalog.API
dotnet run --project src/SimpleStore.Order.API
dotnet run --project src/SimpleStore.Cart.API
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

---

## Database Migrations

Each EF Core DbContext is owned by its API project and migrates itself on startup. To manage migrations manually:

```pwsh
# Catalog
dotnet ef migrations add <Name> --project src/SimpleStore.Catalog.API --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext --output-dir Migrations

# Identity
dotnet ef migrations add <Name> --project src/SimpleStore.Identity.API --startup-project src/SimpleStore.Identity.API --context IdentityDbContext --output-dir Migrations

# Orders
dotnet ef migrations add <Name> --project src/SimpleStore.Order.API --startup-project src/SimpleStore.Order.API --context OrderDbContext --output-dir Migrations
```

`SimpleStore.Cart.API` has no DbContext — Redis schema is implicit.

---

## Build

```pwsh
dotnet build SimpleStore.slnx
```

---

## Technology Stack

| Technology | Usage |
|---|---|
| ASP.NET Core 10 MVC | Customer storefront |
| Blazor Server | Admin dashboard |
| ASP.NET Core Identity (v3 schema) + Fido2 | Authentication & passkeys |
| Entity Framework Core 10 | ORM for catalog/order/identity |
| PostgreSQL + Npgsql | Relational data |
| Redis + StackExchange.Redis | Cart storage |
| .NET Aspire 13 | Orchestration, observability |
| OpenTelemetry (OTLP) | Distributed tracing & metrics |

---

## License

This project is licensed under the [MIT License](LICENSE).
