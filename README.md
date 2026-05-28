# SimpleStore

A **production-grade microservices reference architecture** built with **.NET 10**, **.NET Aspire**, **Entity Framework Core**, **PostgreSQL**, **Redis**, **RabbitMQ**, and **KurrentDB**. Designed as a progressive learning resource for developers studying microservices patterns, this e-commerce platform demonstrates how to evolve from a monolith into a fully distributed system with proper service boundaries, event-driven communication, saga orchestration, and CQRS/Event Sourcing.

> **Who is this for?** Developers learning microservices architecture who want a real, runnable codebase that demonstrates industry patterns — not just theory. Each version (v1–v8b) introduces a new concept you can study incrementally.

---

## Screenshots

### Frontend (Customer Storefront)

![SimpleStore Frontend](img/frontend.png)

### Backend (Admin Dashboard)

![SimpleStore Backend](img/backend.png)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              .NET Aspire AppHost                                 │
│                        (Orchestration & Service Discovery)                       │
└─────────────────────────────────────────────────────────────────────────────────┘
        │                           │                          │
        ▼                           ▼                          ▼
┌──────────────┐          ┌──────────────────┐       ┌────────────────┐
│  SimpleStore │          │  SimpleStore.Web  │       │ SimpleStore    │
│   .Gateway   │◄─────────│  (MVC Storefront) │       │ .Admin         │
│   (YARP)     │◄─────────│                  │       │ (Blazor Server)│
└──────┬───────┘          └──────────────────┘       └────────────────┘
       │
       │  Routes /api/v1/<service>/* to backend services
       │
       ├───────────────┬───────────────┬───────────────┬───────────────┐
       ▼               ▼               ▼               ▼               ▼
┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│  Identity   │ │  Catalog    │ │   Order     │ │    Cart     │ │  Inventory  │
│    API      │ │    API      │ │    API      │ │    API      │ │    API      │
├─────────────┤ ├─────────────┤ ├─────────────┤ ├─────────────┤ ├─────────────┤
│ identitydb  │ │ catalogdb   │ │  orderdb    │ │ cart-redis  │ │ kurrentdb   │
│ (Postgres)  │ │ (Postgres)  │ │ (Postgres)  │ │  (Redis)    │ │  (write/ES) │
└─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘ │ inventorydb │
                                                                 │  (read/CQRS)│
                                                                 └─────────────┘
                                                                        │
       ┌────────────────────────────────────────────────────────────────┘
       │
       ▼
┌──────────────────────────────────────────────────────────────┐
│                    Checkout API (Saga Orchestrator)            │
│           Coordinates order → stock → confirmation            │
│                      via RabbitMQ + MassTransit                │
├──────────────────────────────────────────────────────────────┤
│ checkoutdb (Postgres) — saga state + Quartz scheduled jobs    │
└──────────────────────────────────────────────────────────────┘
```

---

## Key Microservices Patterns Demonstrated

| Pattern | Where It's Applied | What You'll Learn |
|---------|-------------------|-------------------|
| **Database-per-Service** | Every service owns its own database | Data isolation, no shared schemas |
| **API Gateway** | `SimpleStore.Gateway` (YARP) | Single entry point, edge auth, path routing |
| **Backend-for-Frontend (BFF)** | Web & Admin apps | Session management, token caching, cookie-based auth for browsers |
| **Event-Driven Architecture** | RabbitMQ + MassTransit | Loose coupling, async communication between services |
| **Saga Orchestration** | `SimpleStore.Checkout.API` | Long-running workflows without distributed transactions |
| **CQRS + Event Sourcing** | `SimpleStore.Inventory.API` | Separate read/write models, append-only event streams, projections |
| **Transactional Outbox** | Order.API, Inventory.API | Reliable event publishing (atomicity between DB writes and messaging) |
| **Inbox (Idempotency)** | Cart.API | Deduplicate message deliveries |
| **Service Discovery** | Aspire runtime | Dynamic routing via logical service names (no hardcoded ports) |
| **Distributed Tracing** | OpenTelemetry across all services | End-to-end observability across service boundaries |
| **Typed HTTP Clients** | `*.API.Client` libraries | Clean service-to-service communication contracts |
| **Durable Timeouts** | Quartz persistent store (v8b) | Saga timeouts that survive service restarts |

---

## Microservices in Detail

### Identity API (`SimpleStore.Identity.API`)

| Aspect | Details |
|--------|---------|
| **Owns** | `identitydb` (PostgreSQL) |
| **Responsibilities** | User registration, login, JWT issuance (HS256, 15min), refresh token rotation, passkey/WebAuthn support, admin user management |
| **Auth Pattern** | Token provider — all other services validate JWTs issued here |
| **Demo Accounts** | `admin@simplestore.local` / `Admin123!` (Admin), `demo@simplestore.local` / `Demo123!` (Customer) |

### Catalog API (`SimpleStore.Catalog.API`)

| Aspect | Details |
|--------|---------|
| **Owns** | `catalogdb` (PostgreSQL) |
| **Responsibilities** | Product & category CRUD, stock level cache (denormalized from Inventory) |
| **Auth** | Anonymous reads, Admin-only writes |
| **Publishes** | `ProductUpdatedEvent` |
| **Consumes** | `StockLevelChangedEvent` (refreshes cached stock from Inventory) |
| **Pattern** | CRUD service with denormalized read-through cache |

### Order API (`SimpleStore.Order.API`)

| Aspect | Details |
|--------|---------|
| **Owns** | `orderdb` (PostgreSQL) |
| **Responsibilities** | Order creation (status=Pending), status tracking, order history |
| **Auth** | Authenticated users own their orders; Admin endpoints for management |
| **Publishes** | `OrderSubmittedEvent` (triggers the checkout saga) |
| **Consumes** | `OrderConfirmedEvent`, `OrderCancelledEvent` |
| **Pattern** | CRUD + transactional outbox for reliable event publishing |

### Cart API (`SimpleStore.Cart.API`)

| Aspect | Details |
|--------|---------|
| **Owns** | `cart-redis` (Redis) |
| **Responsibilities** | Shopping cart state (anonymous via `X-Cart-Id` header, authenticated via JWT `sub` claim), cart merge on login |
| **Consumes** | `ProductUpdatedEvent` (refreshes denormalized line items) |
| **Pattern** | Cache-backed stateful service with inbox deduplication |

### Inventory API (`SimpleStore.Inventory.API`) — Event Sourced + CQRS

| Aspect | Details |
|--------|---------|
| **Write Side** | KurrentDB (event store) — append-only streams for `DeliveryNote`, `ReceiptNote`, `Reservation` aggregates |
| **Read Side** | `inventorydb` (PostgreSQL) — projected views: stock levels, movements, delivery/receipt notes |
| **Projection** | `InventoryProjectionService` subscribes to KurrentDB `$all` stream, replays events into Postgres |
| **Publishes** | `StockReservedEvent`, `StockReservationFailedEvent`, `StockLevelChangedEvent` |
| **Consumes** | `ReserveStockRequestedEvent` (from checkout saga) |
| **Pattern** | Full CQRS + Event Sourcing with eventual consistency |

### Checkout API (`SimpleStore.Checkout.API`) — Saga Orchestrator

| Aspect | Details |
|--------|---------|
| **Owns** | `checkoutdb` (PostgreSQL) — saga state + Quartz jobs |
| **HTTP Surface** | None — pure message consumer |
| **Responsibilities** | Orchestrates order confirmation workflow across services |
| **Concurrency** | Pessimistic locking (`SELECT ... FOR UPDATE` on saga state) |
| **Timeout** | 30-second reservation timeout (durable via Quartz persistent store) |

### API Gateway (`SimpleStore.Gateway`)

| Aspect | Details |
|--------|---------|
| **Technology** | YARP (Yet Another Reverse Proxy) |
| **Responsibilities** | Routes `/api/v1/<service>/*` to backend services, JWT validation at the edge, per-route authorization policies |
| **Service Discovery** | Uses Aspire service discovery to locate backends dynamically |
| **Pattern** | Defense in depth (edge auth + backend auth) |

---

## Checkout Saga Flow

This is the core distributed workflow that ties multiple services together without a distributed transaction:

```
Customer clicks "Place Order"
         │
         ▼
┌─────────────────────────────┐
│ Order.API creates Order     │
│ (Status = Pending)          │
│ Publishes OrderSubmittedEvent│
│ via transactional outbox    │
└────────────┬────────────────┘
             │ RabbitMQ
             ▼
┌─────────────────────────────┐
│ Checkout.API saga starts    │
│ State → AwaitingStock       │
│ Publishes                   │
│ ReserveStockRequestedEvent  │
└────────────┬────────────────┘
             │ RabbitMQ
             ▼
┌─────────────────────────────┐
│ Inventory.API checks stock  │
│ (SELECT...FOR UPDATE)       │
├─────────────┬───────────────┤
│ Sufficient  │ Insufficient  │
│     │       │       │       │
│     ▼       │       ▼       │
│ StockReserved│ StockReservation│
│ Event       │ FailedEvent    │
└─────┬───────┴───────┬───────┘
      │ RabbitMQ       │
      ▼                ▼
┌──────────────┐ ┌──────────────┐
│ Saga →       │ │ Saga →       │
│ Confirmed    │ │ Cancelled    │
│ Publishes    │ │ Publishes    │
│ OrderConfirmed│ │ OrderCancelled│
│ Event        │ │ Event        │
└──────┬───────┘ └──────┬───────┘
       │ RabbitMQ        │
       ▼                 ▼
┌─────────────────────────────┐
│ Order.API updates status    │
│ (Confirmed or Cancelled)    │
└─────────────────────────────┘

⏱️ If no response within 30s → Saga transitions to Cancelled (timeout)
```

---

## Event-Driven Communication

All integration events are defined in `SimpleStore.Contracts` (a shared library with no other dependencies):

| Event | Publisher | Consumer(s) | Purpose |
|-------|-----------|-------------|---------|
| `OrderSubmittedEvent` | Order.API | Checkout.API | Triggers checkout saga |
| `ReserveStockRequestedEvent` | Checkout.API | Inventory.API | Request stock reservation |
| `StockReservedEvent` | Inventory.API | Checkout.API | Confirm reservation succeeded |
| `StockReservationFailedEvent` | Inventory.API | Checkout.API | Report insufficient stock |
| `OrderConfirmedEvent` | Checkout.API | Order.API | Finalize order as confirmed |
| `OrderCancelledEvent` | Checkout.API | Order.API | Mark order as cancelled |
| `ProductUpdatedEvent` | Catalog.API | Cart.API | Refresh cached product info in carts |
| `StockLevelChangedEvent` | Inventory.API | Catalog.API | Sync stock cache in catalog |

**Messaging Infrastructure**: RabbitMQ with MassTransit (supports transactional outbox/inbox patterns).

---

## Project Structure

```
SimpleStore.slnx
src/
├── SimpleStore.AppHost              # .NET Aspire orchestration (defines all resources & dependencies)
├── SimpleStore.ServiceDefaults      # Shared: OpenTelemetry, resilience, service discovery, health checks
├── SimpleStore.Contracts            # Shared integration event definitions (no other dependencies)
├── SimpleStore.Gateway              # YARP API Gateway (edge routing & auth)
│
├── SimpleStore.Identity.API         # Identity microservice (owns identitydb)
├── SimpleStore.Identity.API.Client  # DTOs + typed HttpClient for Identity
│
├── SimpleStore.Catalog.API          # Catalog microservice (owns catalogdb)
├── SimpleStore.Catalog.API.Client   # DTOs + typed HttpClient for Catalog
│
├── SimpleStore.Order.API            # Order microservice (owns orderdb)
├── SimpleStore.Order.API.Client     # DTOs + typed HttpClient for Order
│
├── SimpleStore.Cart.API             # Cart microservice (Redis-backed)
├── SimpleStore.Cart.API.Client      # DTOs + typed HttpClient for Cart
│
├── SimpleStore.Inventory.API        # Inventory microservice (CQRS + Event Sourcing)
├── SimpleStore.Inventory.API.Client # DTOs + typed HttpClient for Inventory
│
├── SimpleStore.Checkout.API         # Saga orchestrator (MassTransit state machine)
│
├── SimpleStore.Web                  # Customer storefront (ASP.NET Core MVC + Razor Pages)
└── SimpleStore.Admin                # Admin dashboard (Blazor Server)
```

### Data Ownership

| Service | Database | Technology | Notes |
|---------|----------|------------|-------|
| Identity.API | `identitydb` | PostgreSQL | ASP.NET Core Identity tables + refresh tokens |
| Catalog.API | `catalogdb` | PostgreSQL | Products, categories, cached stock levels |
| Order.API | `orderdb` | PostgreSQL | Orders, order items |
| Cart.API | `cart-redis` | Redis | Shopping cart state (ephemeral) |
| Inventory.API | `kurrentdb` + `inventorydb` | KurrentDB + PostgreSQL | Event store (write) + projected views (read) |
| Checkout.API | `checkoutdb` | PostgreSQL | Saga state + Quartz scheduled jobs |

> **No cross-database foreign keys** — `Order.UserId` and `OrderItem.ProductId` are soft references. Cross-service data joins happen in application code (bulk fetch + dictionary lookup).

---

## Reliability & Resilience Patterns

### Transactional Outbox
Events are written to the same database transaction as business data, then delivered to RabbitMQ asynchronously. This guarantees **at-least-once delivery** without two-phase commits.

### Inbox (Idempotency)
Consumer services (e.g., Cart.API) use MassTransit's inbox pattern to deduplicate messages, ensuring exactly-once processing semantics.

### Durable Saga State
The checkout saga state is persisted in PostgreSQL with pessimistic locking (`SELECT ... FOR UPDATE`), preventing race conditions in concurrent event processing.

### Durable Timeouts (v8b)
Saga timeouts use Quartz.NET with a persistent job store in PostgreSQL. Timeouts survive service restarts and use misfire policies for reliability.

### Service Defaults (Cross-Cutting Concerns)
All services call `AddServiceDefaults()` which provides:
- **Service Discovery** — dynamic resolution of service endpoints
- **Resilience** — HTTP client retries, timeouts, circuit breakers (Polly)
- **OpenTelemetry** — distributed tracing, metrics, and structured logging
- **Health Checks** — `/health` (all checks) and `/alive` (liveness only)

---

## Learning Path: Incremental Migration (v1 → v8b)

This project was built incrementally. Each version introduces a new microservices concept:

| Version | Concept Introduced | What You'll Learn |
|---------|-------------------|-------------------|
| **v1** | Database-per-service | Splitting a monolith's data layer into isolated DbContexts |
| **v2** | First extracted service | Moving Catalog into its own API + typed HTTP client |
| **v3** | Authentication service | Extracting Identity.API, introducing JWT bearer tokens |
| **v4** | Full service extraction | Order.API + Cart.API (Redis), completing the decomposition |
| **v5** | API Gateway | Adding YARP as a single entry point, edge authorization |
| **v6** | Event-driven messaging | RabbitMQ + MassTransit, transactional outbox/inbox patterns |
| **v7** | CQRS + Event Sourcing | Inventory.API with KurrentDB write side + Postgres read projections |
| **v8** | Saga orchestration | Checkout.API saga coordinates order → stock → confirmation |
| **v8a** | Production hardening | N+1 query fixes, enum status values, validation annotations |
| **v8b** | Durable timeouts | Quartz persistent store so saga timeouts survive restarts |

> 📖 See the [`docs/`](docs/) folder for detailed change notes for each version.

---

## Technology Stack

| Technology | Role in Architecture |
|---|---|
| **ASP.NET Core 10 MVC** | Customer storefront (BFF pattern) |
| **Blazor Server** | Admin dashboard (BFF pattern) |
| **YARP** | API Gateway — reverse proxy with routing & auth |
| **ASP.NET Core Identity + Fido2** | Authentication, passkeys/WebAuthn |
| **Entity Framework Core 10** | ORM for relational services |
| **PostgreSQL + Npgsql** | Relational data store (per-service databases) |
| **Redis + StackExchange.Redis** | Cart state storage |
| **KurrentDB** | Event store for Inventory write-side (Event Sourcing) |
| **RabbitMQ + MassTransit** | Message broker + transport abstraction (outbox/inbox/saga) |
| **Quartz.NET** | Durable scheduled jobs (saga timeouts) |
| **.NET Aspire 13** | Orchestration, service discovery, observability |
| **OpenTelemetry (OTLP)** | Distributed tracing, metrics & structured logging |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [.NET Aspire workload](https://learn.microsoft.com/dotnet/aspire/fundamentals/setup-tooling)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Aspire uses containers for PostgreSQL, Redis, RabbitMQ, KurrentDB)

Install the Aspire workload:

```pwsh
dotnet workload install aspire
```

---

## Getting Started

### 1. One-time secret setup

The AppHost expects three user-secret parameters. `jwt-key` must be a base64-encoded 32-byte (or longer) array:

```pwsh
dotnet user-secrets set Parameters:jwt-key       "<base64 of 32 random bytes>" --project src/SimpleStore.AppHost
dotnet user-secrets set Parameters:jwt-issuer    "simple-store"                --project src/SimpleStore.AppHost
dotnet user-secrets set Parameters:jwt-audience  "simple-store"                --project src/SimpleStore.AppHost
```

### 2. Run with Aspire (recommended)

Aspire starts all infrastructure (PostgreSQL, Redis, RabbitMQ, KurrentDB) and all services:

```pwsh
dotnet run --project src/SimpleStore.AppHost
```

Open the **Aspire dashboard** URL printed in the console to see:
- All service endpoints and their health
- Distributed traces across service boundaries
- Structured logs from all services
- Metrics and resource consumption

### 3. Run individual services

Each project requires the relevant connection strings / service URIs / `Jwt__*` env vars (normally injected by Aspire):

```pwsh
dotnet run --project src/SimpleStore.Identity.API
dotnet run --project src/SimpleStore.Catalog.API
dotnet run --project src/SimpleStore.Order.API
dotnet run --project src/SimpleStore.Cart.API
dotnet run --project src/SimpleStore.Inventory.API
dotnet run --project src/SimpleStore.Checkout.API
dotnet run --project src/SimpleStore.Gateway
dotnet run --project src/SimpleStore.Web
dotnet run --project src/SimpleStore.Admin
```

---

## Database Migrations

Each EF Core DbContext is owned by its API project and **auto-migrates on startup**. To manage migrations manually:

```pwsh
# Catalog
dotnet ef migrations add <Name> --project src/SimpleStore.Catalog.API `
  --startup-project src/SimpleStore.Catalog.API --context CatalogDbContext --output-dir Migrations

# Identity
dotnet ef migrations add <Name> --project src/SimpleStore.Identity.API `
  --startup-project src/SimpleStore.Identity.API --context IdentityDbContext --output-dir Migrations

# Orders
dotnet ef migrations add <Name> --project src/SimpleStore.Order.API `
  --startup-project src/SimpleStore.Order.API --context OrderDbContext --output-dir Migrations

# Inventory (read side only)
dotnet ef migrations add <Name> --project src/SimpleStore.Inventory.API `
  --startup-project src/SimpleStore.Inventory.API --context InventoryReadDbContext --output-dir Migrations
```

> `Cart.API` uses Redis (no schema). `Inventory.API` write-side uses KurrentDB (append-only, no migrations). `Checkout.API` uses MassTransit + Quartz auto-migration.

---

## Build

```pwsh
dotnet build SimpleStore.slnx
```

---

## Architecture Decisions & Trade-offs

| Decision | Rationale |
|----------|-----------|
| **Soft references** instead of foreign keys across services | Services must be independently deployable; no shared database |
| **Eventual consistency** over strong consistency | Saga + events provide reliability without distributed transactions (2PC) |
| **Denormalized stock in Catalog** | Avoids synchronous calls to Inventory on every product read |
| **CQRS for Inventory only** | Not every service needs event sourcing — only where audit trail and temporal queries add value |
| **Pessimistic locking for saga state** | Prevents duplicate saga processing from concurrent message delivery |
| **BFF for browser clients** | Tokens never reach the browser; session cookies + server-side token caching |
| **Gateway validates JWT at edge** | Defense in depth — backend services also validate, but gateway rejects early |

---

## Further Reading

- [`docs/checkout-saga.md`](docs/checkout-saga.md) — Detailed checkout saga design
- [`docs/v1-changes.md`](docs/v1-changes.md) through [`docs/v8b-durable-store-for-saga-timeouts.md`](docs/v8b-durable-store-for-saga-timeouts.md) — Version-by-version migration notes
- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire/)
- [MassTransit Saga Documentation](https://masstransit.io/documentation/patterns/saga)
- [CQRS & Event Sourcing (Martin Fowler)](https://martinfowler.com/bliki/CQRS.html)
- [Saga Pattern (Microsoft)](https://learn.microsoft.com/azure/architecture/reference-architectures/saga/saga)

---

## License

This project is licensed under the [MIT License](LICENSE).
