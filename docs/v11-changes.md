# v11 Changes — API & Event Versioning

## Overview

[v10](v10-changes.md) made the system observable. v11 makes its contracts evolvable. Two surfaces in the system needed it:

- **HTTP APIs.** The gateway hardcoded `/api/v1/<service>/...` and stripped `/v1` with a path transform; backends served unversioned `/api/<service>/...`. There was no path to host `v1` and `v2` of any endpoint side-by-side, no `Asp.Versioning.*` package, and no version-aware OpenAPI. A v2 endpoint would have meant forking gateway routes, forking backend routes, and rewriting clients.
- **Integration events.** The 8 records in `SimpleStore.Contracts` had no `Vn` suffix, no `Version` field, no pinned `MessageUrn`. MassTransit auto-derived the wire URN from `FullName` — renaming the CLR type would have silently broken the wire. System.Text.Json (Web defaults) tolerated added fields, but anything else would have broken.

v11 is a single cross-cutting pass — same shape as v9 (resilience) and v10 (observability) — that lets both surfaces evolve without breaking consumers. No new business features, no schema changes.

The policy doc that came out of this work is [versioning.md](versioning.md).

---

## 1. `Asp.Versioning.Http` adopted across all 5 backends

**Files:**
- `src/SimpleStore.ServiceDefaults/SimpleStore.ServiceDefaults.csproj` — `Asp.Versioning.Http` + `Asp.Versioning.Mvc.ApiExplorer` packages added at version 8.1.0.
- `src/SimpleStore.ServiceDefaults/ApiVersioningExtensions.cs` — **new** helper hosting `AddSimpleStoreApiVersioning()` + `MapApiV1Group(serviceSegment)`.
- `src/SimpleStore.Identity.API/Program.cs`, `src/SimpleStore.Catalog.API/Program.cs`, `src/SimpleStore.Order.API/Program.cs`, `src/SimpleStore.Cart.API/Program.cs`, `src/SimpleStore.Inventory.API/Program.cs` — each one adds `builder.AddSimpleStoreApiVersioning()` next to `AddServiceDefaults()`, and switches `AddOpenApi()` to `AddOpenApi("v1")` so future versions slot in as `AddOpenApi("v2", ...)`.
- `src/SimpleStore.Identity.API/Endpoints/IdentityEndpoints.cs`, `src/SimpleStore.Catalog.API/Endpoints/CatalogEndpoints.cs`, `src/SimpleStore.Order.API/Endpoints/OrderEndpoints.cs`, `src/SimpleStore.Cart.API/Endpoints/CartEndpoints.cs`, `src/SimpleStore.Inventory.API/Endpoints/InventoryEndpoints.cs` — `MapGroup("/api/<service>")` → `MapApiV1Group("<service>")`.

**Problem:** versioning was hardcoded at the gateway as a string transform. Backends had no awareness of API version at all. There was no `ApiVersionSet`, no `IApiVersionDescriptionProvider`, no deprecation / sunset primitives.

**Fix:** adopt the Microsoft `Asp.Versioning` library, URL-segment reader, library packages added once in `ServiceDefaults` and inherited transitively by every service that calls `AddServiceDefaults`. Each backend then needs two one-liners:

```csharp
builder.AddSimpleStoreApiVersioning();        // Program.cs, next to AddServiceDefaults()
// ...
app.MapApiV1Group("catalog");                 // Endpoints/*Endpoints.cs, replaces MapGroup("/api/catalog")
```

`MapApiV1Group` (in `ApiVersioningExtensions.cs`) builds a v1 `ApiVersionSet`, maps a group at `/api/v{version:apiVersion}/<service>`, and pins it to v1.0. With `ReportApiVersions = true` set globally, every response carries `api-supported-versions: 1.0` and `api-deprecated-versions:` headers so clients can discover what the server supports without parsing OpenAPI.

**Impact:** adding a v2 endpoint to any service is now a localized change — declare a new `ApiVersion(2, 0)` on the version set, `MapToApiVersion(2, 0)` on the new endpoints, and v1 stays live in parallel. Deprecating a version is `versionSet.HasDeprecatedApiVersion(...)` which emits a `Sunset` header automatically.

---

## 2. Gateway forwards versioned paths without rewriting

**Files:**
- `src/SimpleStore.Gateway/appsettings.json` — every `"Transforms": [...]` array removed from all 20+ routes.
- `src/SimpleStore.AppHost/AppHost.cs` — the gateway-comment about "path transform strips /v1/" updated to describe the new pass-through behavior.

**Problem:** because backends served `/api/catalog` while the public URL was `/api/v1/catalog`, the gateway carried the entire version contract in a YARP `PathPattern` transform. Any v2 rollout would have meant editing the gateway config in lockstep with each backend.

**Fix:** with §1, backends now serve `/api/v{version}/catalog/...` directly. The gateway's `PathPattern` transform becomes identity, so it is simply removed. Public URL and backend URL are the same string from `/api/v1/` onward.

```jsonc
"catalog-read": {
    "ClusterId": "catalog-cluster",
    "Match": { "Path": "/api/v1/catalog/{**catch-all}", "Methods": [ "GET", "HEAD" ] }
    // Transforms array removed — backend serves /api/v1/catalog/... natively
}
```

Edge auth (`AuthorizationPolicy`) and route matching stay unchanged — the gateway still does defense-in-depth JWT validation and per-route policy enforcement, exactly as it did in v5–v10.

**Impact:** the version contract now lives with the API code, not the proxy config. `https://<backend>/api/v1/catalog/products` and `https://<gateway>/api/v1/catalog/products` reach the same handler. Direct backend calls (dev loop, integration tests, the Aspire dashboard) use the same URL shape as the public one.

---

## 3. Per-version OpenAPI documents

**Files:** the 5 backend `Program.cs` files in §1.

**Problem:** `AddOpenApi()` (no arguments) registers a single document under the default name `"v1"`. That URL string was a coincidence, not a contract — and there was no way to add a `v2.json` without renaming the existing one.

**Fix:** every backend now calls `AddOpenApi("v1")` explicitly. The Asp.Versioning `ApiExplorer` integration (configured in `AddSimpleStoreApiVersioning` with `GroupNameFormat = "'v'VVV"`) tags every `ApiDescription` with a group name like `v1`, and `.NET 10`'s `AddOpenApi("v1", ...)` filters to that group. A future `AddOpenApi("v2", ...)` would produce a `/openapi/v2.json` filtered to v2 endpoints, without touching the v1 doc.

```csharp
builder.Services.AddOpenApi("v1");           // every backend
// app.MapOpenApi() defaults to /openapi/{documentName}.json → /openapi/v1.json
```

**Impact:** OpenAPI is now per-version by construction. The `v1.json` document published in dev reflects only v1 endpoints; the `Sunset` / `api-supported-versions` headers from §1 are emitted with every response.

---

## 4. Integration events: `Vn` suffix + `Version` field + pinned `MessageUrn`

**Files:**
- `src/SimpleStore.Contracts/SimpleStore.Contracts.csproj` — adds `MassTransit.Abstractions` 8.5.2 for `[MessageUrn]`.
- `src/SimpleStore.Contracts/*.cs` (all 8 event records) — each one renamed with a `V1` suffix, given an `int Version { get; init; } = 1;` field, and pinned with `[MessageUrn("urn:message:SimpleStore.Contracts:<OriginalTypeName>")]`.
- Every publisher and consumer across `Order.API`, `Catalog.API`, `Cart.API`, `Inventory.API`, `Checkout.API` (`Consumers/*.cs`, `Services/OrderService.cs`, `Services/CatalogService.cs`, `Application/Reservations/CreateReservationHandler.cs`, `Projections/InventoryProjector.cs`, `Sagas/CheckoutSagaStateMachine.cs`) — references updated to the `V1`-suffixed type names.

**Problem:** before v11, MassTransit derived the wire URN from `FullName` automatically (e.g. `urn:message:SimpleStore.Contracts:OrderSubmittedEvent`). The CLR type name **was** the contract. Three failure modes followed:

1. Renaming the CLR type (refactor, namespace move) silently broke the bus — no in-flight messages could correlate.
2. A breaking shape change had no version distinction at all. Old and new consumers might both try to deserialize the same wire URN with incompatible expectations.
3. No introspection — a consumer log line couldn't report "this is v1 of the event", only the type name.

**Fix:** make the wire URN explicit and decoupled from the CLR type.

```csharp
[MessageUrn("urn:message:SimpleStore.Contracts:OrderSubmittedEvent")] // pinned to pre-v11 default
public sealed record OrderSubmittedEventV1
{
    public int Version { get; init; } = 1;
    public Guid CorrelationId { get; init; }
    // ... existing fields unchanged
}
```

The pinned URN string is **literally the MassTransit default for the pre-v11 CLR type name**. That keeps the wire backward-compatible — RabbitMQ queue topology stays valid, in-flight messages still route, the rename is invisible on the bus. When a `V2` ships, it gets a new URN (e.g. `urn:message:SimpleStore.Contracts:OrderSubmittedEventV2`) and routes to its own consumers.

The `Version` int field is set to `1` by default. System.Text.Json fills missing JSON properties with the C# default initializer, so any pre-v11 payload arriving on the bus during a rolling deploy deserializes as `Version = 1` — no migration step, no replay.

Nested DTOs (`OrderSubmittedLineItem`, `ReservationLineItem`, `ShortageLine`) deliberately stay un-suffixed — they version with their parent event, same convention as Inventory's `StockReservedV1.LineData`.

**Impact:** the 8 events covered: `OrderSubmittedEventV1`, `OrderConfirmedEventV1`, `OrderCancelledEventV1`, `ProductUpdatedEventV1`, `ReserveStockRequestedEventV1`, `StockReservedEventV1`, `StockReservationFailedEventV1`, `StockLevelChangedEventV1`. The next breaking change to any of them produces a new V2 type that ships alongside the V1 — no field renames, no field-meaning changes, no surprises for old consumers.

---

## 5. Inventory domain events: upcaster scaffold + unknown-event metric

**Files:**
- `src/SimpleStore.Inventory.API/EventStore/IEventUpcaster.cs` — **new** interface (scaffold, not wired).
- `src/SimpleStore.Inventory.API/EventStore/EventTypeRegistry.cs` — extended doc-comment with a step-by-step V2-introduction recipe.
- `src/SimpleStore.Inventory.API/Observability/Telemetry.cs` — adds `simplestore.inventory.projector.unknown_events` counter.
- `src/SimpleStore.Inventory.API/Projections/InventoryProjectionService.cs` — increments the counter when the projector hits an unknown wire type; adds `inventory.unknown_event` activity tag for trace filtering.

**Problem:** the KurrentDB convention introduced in v7 (`Vn`-suffixed CLR type + `simplestore.<context>.<aggregate>.<verb>.vN` wire string + `EventTypeRegistry` bidirectional map) was solid. But:

- There was no documented recipe for introducing a V2. A future maintainer would have to reverse-engineer it.
- `InventoryProjectionService.ApplyOneAsync` already logged + checkpointed past unknown event types (good — forward-compatible by design), but a botched V2 rollout was silent past one WARN log line. No metric to alert on; no trace tag to filter on.

**Fix:**

```csharp
// IEventUpcaster.cs — scaffold; not registered in DI yet (no V2 exists in v11).
public interface IEventUpcaster<TOld, TNew>
    where TOld : IInventoryDomainEvent
    where TNew : IInventoryDomainEvent
{
    TNew Upcast(TOld old);
}
```

The interface is intentionally tiny. An upcaster is meant to be a pure transform — if it needs to read state, the change isn't a pure upcast and probably wants a one-shot migration projection instead. The `EventTypeRegistry.cs` doc-comment now spells out the 5-step V2 rollout (add type, register wire string, add Apply overload, optionally implement `IEventUpcaster<TOld, TNew>`, replay if needed).

```csharp
// Telemetry.cs — new counter
public static readonly Counter<long> UnknownEvents = Meter.CreateCounter<long>(
    "simplestore.inventory.projector.unknown_events",
    description: "Count of events whose wire type is not registered in EventTypeRegistry. Should be 0 in steady state.");

// InventoryProjectionService.cs — increment when DomainEvent is null
activity?.SetTag("inventory.unknown_event", envelope.Type);
Telemetry.UnknownEvents.Add(1, new KeyValuePair<string, object?>("event_type", envelope.Type));
```

**Impact:** the projector still gracefully skips unknown events (cold-replay-friendly forward compat from v7), but the metric makes a botched deploy noisy. A non-zero rate on `simplestore.inventory.projector.unknown_events` means "a new event type is in the stream and this replica doesn't know about it" — usually a deploy ordering bug (new writer rolled out before new reader). The `event_type` tag lets the dashboard alert name the offending wire string, and the `inventory.unknown_event` activity tag joins the related trace.

---

## 6. Policy doc

**File:** [versioning.md](versioning.md) — **new**.

A short policy doc covering all three streams (HTTP APIs, integration events, Inventory domain events). It states the rule for additive vs breaking changes, deprecation, replay/upcaster choice for the event-sourced side, and the recipe for adding each new version. The intent is that the next person making a contract change reads one page, not five.

---

## What v11 deliberately did NOT do

- No new endpoints. The v1 surface is exactly the same as v10's; only the URL shape changed (gateway transform → backend native).
- No new events. The 8 integration events kept their fields verbatim — only their CLR type names changed, wire URNs stayed pinned to pre-v11 defaults.
- No new domain events. The Inventory KurrentDB streams are byte-for-byte unchanged.
- No client renames. The 5 `*.API.Client` libraries still call `api/v1/...` exactly as before.
- No upcaster registrations. `IEventUpcaster<TOld, TNew>` is a scaffold; the first concrete implementation will land alongside the first V2 event, whenever that's needed.

The goal was to make the **next** contract change cheap, not to make a change for its own sake.
