# v7 Changes — Add Inventory API with Event-Sourced CQRS

## Overview

Version 7 introduces **`SimpleStore.Inventory.API`**, the first service in SimpleStore that is built explicitly around **Event Sourcing** and **CQRS**.

This is a major architectural step. Earlier versions extracted business capabilities such as Identity, Catalog, Order, and Cart into separate services. v7 goes further by changing **how one service models state internally**:

- the **write side** stores facts as domain events in **KurrentDB**
- the **read side** stores query-friendly views in **Postgres**
- an asynchronous projector keeps the read model updated
- inventory becomes a standalone bounded context responsible for stock movement history

In practical terms, v7 adds support for:

- **delivery notes** for stock going out
- **receipt notes** for stock coming in
- **stock level views** derived from event history
- **stock movement history** derived from event history

This version is important because it shows learners that microservices are not only about splitting projects. They are also about choosing the right **data model and consistency model** for each business capability.

---

## Why This Matters

### 1. CQRS is being applied where reads and writes have different needs

Inventory is a good fit for CQRS because the system has two very different jobs:

- **write job:** record business facts such as “a receipt note was recorded” or “a delivery note was issued”
- **read job:** answer questions such as “what is the current stock for product 42?” or “show me the movement history”

Those jobs want different storage shapes.

A write model wants strong business rules and a clear audit trail. A read model wants fast queries and simple tables. v7 separates them instead of forcing one model to serve both purposes.

From the new service startup:

```csharp
builder.AddNpgsqlDbContext<InventoryReadDbContext>("inventorydb");
builder.Services.AddSingleton<IEventStore, KurrentEventStore>();
builder.Services.AddHostedService<InventoryProjectionService>();
```

This is the essence of CQRS in the diff:

- **commands** go to the event store
- **queries** go to the read database
- the two sides are connected asynchronously

### 2. Event Sourcing makes the event log the source of truth

Instead of updating a single `Stock` field directly, v7 records **what happened**.

Examples:

- `DeliveryNoteIssuedV1`
- `ReceiptNoteRecordedV1`

That means the system preserves the business history, not just the latest number.

This matters because inventory is naturally event-shaped:

- stock arrives
- stock leaves
- audits need history
- future features often depend on replaying or reinterpreting past facts

The diff makes this explicit in the read model comments:

```csharp
// The truth lives in the event store as the sequence of DeliveryNoteIssuedV1
// and ReceiptNoteRecordedV1 events. Wipe both Postgres tables and the
// projector will rebuild them by replaying the event store from the start.
```

That is one of the clearest educational lessons in v7: **the database used for queries is no longer the source of truth**.

### 3. Eventual consistency becomes a visible architectural concept

In CRUD systems, developers often assume “write then immediately read” will always return the new state.

v7 deliberately teaches that this is not always true in distributed systems. The POST endpoints return data from the in-memory aggregate, while the GET endpoints query the projected read model.

```csharp
// POST returns the DTO from the in-memory aggregate state, NOT the read DB.
// The projector is async; reading back through GET microseconds later may
// briefly 404 while the projector catches up.
```

That is not a bug. It is the trade-off that comes with asynchronous CQRS designs.

### 4. The service boundary is stronger than before

Inventory is not just “another table” added to Catalog or Order. It becomes its own microservice with:

- its own API
- its own write model
- its own read model
- its own event store
- its own Postgres database
- its own future integration path

This is a stronger version of microservice autonomy: the service owns not only its code and data, but also its **consistency model**.

---

## What Changed

### 1. A new `SimpleStore.Inventory.API` project

v7 adds a brand-new service dedicated to inventory management.

Its structure is intentionally layered:

- `Application/` — command handlers
- `Domain/` — aggregates and domain events
- `EventStore/` — event store abstraction and KurrentDB adapter
- `Projections/` — asynchronous projector and checkpointing
- `Data/ReadModels/` — query-side tables
- `Endpoints/` — HTTP API

This structure matters because it teaches learners that CQRS + Event Sourcing usually needs **clear separation of responsibilities**.

The new service is also admin-only in v7:

```csharp
var group = app.MapGroup("/api/inventory").RequireAuthorization("Admin");
```

Why? Because inventory changes are operational back-office actions, not public storefront actions. That keeps the first version focused and reduces accidental complexity while the new architecture is being introduced.

#### New API surface

The service exposes three main areas:

- `/api/inventory/delivery-notes`
- `/api/inventory/receipt-notes`
- `/api/inventory/stock`

This gives operators both:

- **write endpoints** for creating notes
- **read endpoints** for querying stock and movement history

That separation is central to the design.

---

### 2. Event Sourcing implementation: event store, event types, and streams

#### KurrentDB becomes the write-side database

Instead of writing inventory state directly into Postgres, v7 appends events to **KurrentDB**.

Aspire orchestration now provisions an event store container:

```csharp
var kurrentdb = builder.AddKurrentDB("kurrentdb")
    .WithDataVolume("kurrentdb-data");
```

And the service is wired to use it:

```csharp
var inventory = builder.AddProject<Projects.SimpleStore_Inventory_API>("inventory")
    .WithReference(inventoryDb)
    .WithReference(kurrentdb);
```

This matters because event sourcing needs a storage system optimized for ordered event streams, not just relational rows.

#### The write model stores domain events, not current state rows

The inventory bounded context introduces two domain events:

- `DeliveryNoteIssuedV1`
- `ReceiptNoteRecordedV1`

From the diff:

```csharp
public const string DeliveryNoteIssuedV1Type = "simplestore.inventory.delivery-note.issued.v1";
public const string ReceiptNoteRecordedV1Type = "simplestore.inventory.receipt-note.recorded.v1";
```

These event type strings matter for two reasons:

1. they give events a stable wire identity in the event store
2. they establish a versioning strategy with the `.v1` suffix

This is an important lesson: when events are persisted, they become long-lived contracts. Naming and versioning matter much more than in an internal CRUD model.

#### One stream per aggregate instance

The service uses explicit stream names:

- `deliveryNote-{guid}`
- `receiptNote-{guid}`

Example from the command handlers:

```csharp
var stream = $"deliveryNote-{note.Id}";
await _eventStore.AppendAsync(stream, note.UncommittedEvents, AppendCondition.NoStream, ct);
```

This is educational because it shows how event-sourced systems organize history:

- each aggregate gets its own stream
- appending to the stream is the state change
- current state is reconstructed from past events when needed

#### The event store is abstracted behind a port

The diff adds an `IEventStore` abstraction and keeps KurrentDB-specific code in `KurrentEventStore.cs`.

```csharp
public interface IEventStore
{
    Task AppendAsync(string streamName, IReadOnlyList<IInventoryDomainEvent> events, AppendCondition condition, CancellationToken ct);
    IAsyncEnumerable<EventEnvelope> SubscribeAllAsync(string[] streamNamePrefixes, EventStorePosition? fromPosition, CancellationToken ct);
    IAsyncEnumerable<EventEnvelope> ReadStreamAsync(string streamName, CancellationToken ct);
}
```

Why is this important?

Because a bounded context should not leak storage technology details everywhere. By isolating the KurrentDB adapter, the service keeps most of its code focused on business concepts rather than vendor APIs.

#### Optimistic concurrency is explicit

The service uses `AppendCondition.NoStream` when creating new notes.

That means:

- a note can only be created once
- retries with the same client-supplied `Guid` collapse to the same stream
- duplicates become a clean `409 Conflict`

This is a good example of how idempotency is handled in event-sourced systems: not by “checking if a row exists” everywhere, but by treating the stream append as the authoritative concurrency check.

---

### 3. CQRS separation: command handlers vs query handlers

#### Write side: explicit command handlers

The write side is modeled through application commands and handlers:

- `CreateDeliveryNoteCommand` / `CreateDeliveryNoteHandler`
- `CreateReceiptNoteCommand` / `CreateReceiptNoteHandler`

The command handler flow is very explicit in the diff:

```csharp
var note = DeliveryNote.Issue(
    noteId: cmd.NoteId,
    date: cmd.Date,
    reference: cmd.Reference,
    lines: domainLines,
    now: _clock.GetUtcNow());

var stream = $"deliveryNote-{note.Id}";
await _eventStore.AppendAsync(stream, note.UncommittedEvents, AppendCondition.NoStream, ct);
```

This is valuable for learners because it shows a classic CQRS write pipeline:

1. receive intent from the API
2. validate business rules
3. build the aggregate
4. produce domain events
5. persist events
6. return a response without depending on the read model

The domain rules live in the aggregate, not in the controller-style endpoint.

For example, the aggregate rejects:

- empty IDs
- empty note lines
- duplicate `ProductId` lines on the same note

That is exactly what a strong write model should do.

#### Query side: direct reads from read-model tables

There are no separate “query handler” classes yet. Instead, query endpoints read directly from `InventoryReadDbContext`.

For example:

```csharp
var query = db.StockLevels.AsNoTracking().OrderBy(s => s.ProductId);
```

This is still CQRS.

Why? Because CQRS is about **separating write and read models**, not about requiring every query to have a dedicated handler class. In v7, the query side is intentionally lightweight:

- query endpoints
- EF Core read models
- no aggregate reconstruction
- no event store access for reads

This is a useful teaching point: CQRS is a spectrum. This version uses a simple query side because the educational goal is to show the architectural split without over-engineering it.

#### The write side and read side return different kinds of truth

A subtle but important design choice appears in the POST endpoints. After writing, the response is built from the aggregate in memory, not from the projected database.

That teaches a deep CQRS idea:

- **write-side truth:** “the command was accepted and the events were stored”
- **read-side truth:** “the projection has caught up and the query model now reflects those events”

Those are related, but not identical in time.

---

### 4. Read model projections

#### A background projector populates Postgres

The new `InventoryProjectionService` subscribes to the event log and updates query tables asynchronously.

```csharp
await foreach (var envelope in eventStore
    .SubscribeAllAsync(StreamPrefixes, checkpoint, stoppingToken)
    .WithCancellation(stoppingToken))
{
    await ApplyOneAsync(envelope, stoppingToken);
}
```

This is the heart of the CQRS design. The read side is not updated by the API endpoints directly. It is updated by replaying domain events.

#### The read model is optimized for queries

`InventoryReadDbContext` defines tables such as:

- `delivery_notes`
- `delivery_note_lines`
- `receipt_notes`
- `receipt_note_lines`
- `stock_levels`
- `stock_movements`
- `projection_checkpoints`

These are not aggregates. They are denormalized, query-oriented tables.

For example:

- `DeliveryNoteRow` stores `LineCount` and `TotalQuantity`
- `StockLevelRow` stores current `OnHand`
- `StockMovementRow` stores signed deltas for audit history

That design answers an important question for learners: if the event store is the truth, why have Postgres at all?

Answer: because users and APIs still need efficient queries. Projection tables are the **read-optimized cache** built from the event history.

#### Stock is computed, not directly edited

The projector updates stock like this:

```csharp
level.OnHand += delta;
```

But that line happens inside projection logic, not business command logic. That distinction matters.

The system is saying:

- business facts are primary
- stock totals are derived
- if the derived values are lost, they can be rebuilt

That is one of the clearest Event Sourcing lessons in the whole diff.

#### Checkpointing makes replay safe and resumable

The projector stores its last processed position in `projection_checkpoints`.

```csharp
await checkpoints.UpsertAsync(ProjectionName, pos, _clock.GetUtcNow(), ct);
```

This solves an operational problem: how does the projector know where to resume after a crash or restart?

The answer is a persisted bookmark into KurrentDB’s `$all` stream. Without checkpointing, the service would either miss events or replay too much work on every restart.

#### Idempotency is built into projection logic

The projector checks whether a note has already been projected before inserting rows.

That matters because event-driven systems must tolerate retries and restarts. A crash can happen after an event is stored but before the read model transaction finishes. Re-applying the same event safely is therefore essential.

---

### 5. Integration with existing services via messaging and service boundaries

This section is especially educational because v7 changes the architecture **without fully wiring inventory into the existing RabbitMQ flows yet**.

#### Important: v7 intentionally does **not** join the existing MassTransit event flows

The diff explicitly says inventory is standalone for now:

```csharp
// Inventory runs as its own microservice with an event-sourced write side (KurrentDB)
// and a CQRS Postgres read side (inventorydb). v7 is standalone: no RabbitMQ wiring,
// no Catalog/Order references. v8 will add a MassTransit consumer for OrderSubmittedEvent.
```

This is an important design lesson.

The team did **not** try to do everything at once. Instead, v7 establishes the inventory bounded context first:

- define the domain
- define the write model
- define the read model
- prove projections and replay work
- then integrate it into broader event workflows later

That is usually the safer migration strategy in real systems.

#### What integration does exist in v7?

Even though RabbitMQ messaging is not wired yet, inventory is still integrated in two important ways:

1. **through the API gateway**
2. **through a typed client library**

Gateway route added in `SimpleStore.Gateway/appsettings.json`:

```json
"inventory-admin": {
  "ClusterId": "inventory-cluster",
  "AuthorizationPolicy": "Admin",
  "Match": { "Path": "/api/v1/inventory/{**catch-all}" },
  "Transforms": [ { "PathPattern": "/api/inventory/{**catch-all}" } ]
}
```

Typed client added in `SimpleStore.Inventory.API.Client`:

```csharp
public static IHttpClientBuilder AddInventoryApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "gateway")
```

This is useful pedagogically because it shows two different kinds of integration:

- **asynchronous integration** via messaging already exists elsewhere in the system
- **synchronous integration** via HTTP/gateway is used here while the service is still being introduced

In other words, v7 is building the service boundary first, then preparing the service for future event-driven coordination.

#### Why not immediately consume `OrderSubmittedEvent`?

Because that would mix two hard changes together:

- introducing Event Sourcing/CQRS
- moving system-wide stock ownership

v7 chooses to separate those concerns. That keeps the architectural lesson focused and lowers migration risk.

---

### 6. Changes to Aspire orchestration

The AppHost changes are a big part of the story because they show how microservice infrastructure evolves alongside code.

#### New infrastructure resources

Aspire now provisions:

- `inventorydb` in Postgres
- `kurrentdb` for the event store
- service references from gateway to inventory

From `AppHost.cs`:

```csharp
var inventoryDb = postgres.AddDatabase("inventorydb");

var kurrentdb = builder.AddKurrentDB("kurrentdb")
    .WithDataVolume("kurrentdb-data");
```

This matters because CQRS + Event Sourcing usually needs **multiple persistence technologies**:

- one optimized for append-only event streams
- one optimized for relational queries

The orchestration layer must reflect that.

#### The gateway is updated to expose inventory

The gateway now references the inventory service and adds an admin-only route. This keeps the system consistent with the existing platform rule: UIs talk to the gateway, not directly to backend services.

That reinforces a broader microservices principle already present in earlier versions:

- backend services remain independently deployable
- entry-point policy stays centralized in the gateway
- UIs do not need to know internal service topology

#### Solution and package updates

The diff also adds:

- `SimpleStore.Inventory.API`
- `SimpleStore.Inventory.API.Client`
- `CommunityToolkit.Aspire.Hosting.KurrentDB`
- `KurrentDB.Client`

These changes are important because architecture is not just code patterns. It also requires platform support, SDK support, and local development orchestration.

---

## Architecture Diagram

```text
                    +-----------------------------------+
                    |          Admin / Gateway          |
                    |  POST /delivery-notes             |
                    |  POST /receipt-notes              |
                    |  GET  /stock                      |
                    +----------------+------------------+
                                     |
                                     v
                    +-----------------------------------+
                    |      SimpleStore.Inventory.API    |
                    |                                   |
                    |  WRITE SIDE                       |
                    |  - Endpoint -> Command            |
                    |  - Aggregate validates rules      |
                    |  - Emit domain event(s)           |
                    +----------------+------------------+
                                     |
                                     v
                    +-----------------------------------+
                    |           KurrentDB               |
                    |  Streams:                         |
                    |  - deliveryNote-{id}             |
                    |  - receiptNote-{id}              |
                    |  Source of truth                  |
                    +----------------+------------------+
                                     |
                       SubscribeAllAsync + checkpoint
                                     |
                                     v
                    +-----------------------------------+
                    |     InventoryProjectionService    |
                    |  - Reads event log                |
                    |  - Applies idempotent projection  |
                    |  - Stores checkpoint              |
                    +----------------+------------------+
                                     |
                                     v
                    +-----------------------------------+
                    |          Postgres inventorydb     |
                    |  Read model tables:               |
                    |  - delivery_notes                 |
                    |  - receipt_notes                  |
                    |  - stock_levels                   |
                    |  - stock_movements                |
                    |  - projection_checkpoints         |
                    +----------------+------------------+
                                     |
                                     v
                    +-----------------------------------+
                    |            Query Endpoints        |
                    |  GET /delivery-notes             |
                    |  GET /receipt-notes              |
                    |  GET /stock                      |
                    |  GET /stock/{id}/movements       |
                    +-----------------------------------+
```

### How to read the diagram

1. A command enters through an admin endpoint.
2. The write side validates business rules and appends domain events.
3. KurrentDB stores the event stream as the source of truth.
4. A background projector subscribes to those events.
5. The projector updates Postgres read tables.
6. Query endpoints read from Postgres, not from the event store.

That is textbook CQRS with Event Sourcing.

---

## Key Takeaways

### 1. Event Sourcing stores **facts**, not just current values

Instead of storing only “stock is 12,” the system stores the reasons stock became 12.

That gives:

- auditability
- replayability
- better debugging
- easier future projections

### 2. CQRS is about model separation, not just class naming

v7 proves you can have CQRS even when the query side is simple. The key is that reads and writes use **different models and different storage paths**.

### 3. Read models are disposable

Because the event log is authoritative, the read database can be rebuilt. That is a very different mindset from CRUD systems, where the relational database is usually treated as irreplaceable truth.

### 4. Eventual consistency is a design property, not an accident

The write is durable once events are appended. The read side may lag briefly. That trade-off is part of the architecture and must be understood by developers and API consumers.

### 5. Incremental migration is often the right strategy

v7 does not immediately move all stock logic across the whole platform. It first introduces the inventory bounded context cleanly. That is a realistic and disciplined way to evolve a microservice system.

### 6. Service autonomy can include different persistence models

Inventory now uses:

- **KurrentDB** for write history
- **Postgres** for read models

That shows an advanced microservices principle: different services, and even different sides of the same service, can use different storage models when the domain demands it.

---

## Trade-offs

### Benefits

#### Strong audit trail
Every stock movement is backed by explicit business events.

#### Rebuildable read models
If query tables change shape, they can be regenerated from the event log.

#### Better fit for inventory workflows
Inventory naturally revolves around movement history, not just current balances.

#### Clear separation of concerns
Commands enforce business rules. Queries serve optimized views.

#### Future extensibility
The model is ready for richer inventory workflows, more projections, and deeper messaging integration in later versions.

### Costs and complexities

#### More moving parts
The service now depends on both KurrentDB and Postgres, plus a background projector.

#### Eventual consistency
A write may succeed before the read model reflects it.

#### Operational complexity
Checkpointing, replay behavior, and projection health now matter in production.

#### Harder mental model
Developers must understand aggregates, streams, projections, and asynchronous updates.

#### Event versioning responsibility
Once events are persisted, changing them carelessly becomes dangerous.

### Why the trade-off is worth it here

For simple CRUD domains, this architecture would be overkill. But inventory is not a simple CRUD domain. It benefits from:

- historical traceability
- derived stock calculations
- future replay and audit needs
- clear separation between operational writes and reporting reads

That makes v7 a strong teaching example of **when CQRS and Event Sourcing are justified**.

---

## Final Summary

v7 is the version where SimpleStore stops treating every service like a standard CRUD API.

With `SimpleStore.Inventory.API`, the project introduces:

- a true **event-sourced write model**
- a true **CQRS read model**
- explicit **projection and replay** behavior
- an **event store** alongside a relational read database
- a cleaner path toward future event-driven inventory ownership

For learners, this is the version that demonstrates an important architectural shift:

> microservices are not only about splitting the system into services; they are also about choosing the right model of truth, consistency, and storage for each business capability.
