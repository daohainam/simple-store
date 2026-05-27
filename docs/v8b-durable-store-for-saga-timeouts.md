# v8b Changes — Durable Store for Saga Timeouts

## Overview

The checkout saga (`SimpleStore.Checkout.API`) bounds its wait for an inventory decision with a **30-second timeout**. When the saga enters `AwaitingStock` it schedules a `ReservationTimeoutExpired` message; if Inventory never replies with `StockReservedEvent` / `StockReservationFailedEvent`, the timeout fires and the saga cancels the order.

In v8 that timer ran on Quartz's **in-memory `RAMJobStore`**. The scheduled trigger lived only in the process heap, so it had one fatal weakness: **it did not survive a Checkout.API restart.** If the process recycled (deploy, crash, scale event) while a saga was in `AwaitingStock`, the pending timeout was gone. If Inventory then also failed to respond, the order was stranded in `Pending` forever — no timeout, no cancellation, no recovery.

v8b resolves this by moving Quartz to a **persistent ADO (database-backed) job store in `checkoutdb`**. The timer now lives in Postgres next to the saga state, so it survives restarts.

---

## Why This Matters

A saga timeout is a *durability promise*: "if nothing else happens, I guarantee this order is resolved within 30 seconds." A timer that evaporates on restart silently breaks that promise. The failure is invisible — no error is logged, the order just never moves — which makes it the worst kind of bug to diagnose in production.

This is also a classic distributed-systems lesson: **orchestration state and the timers that drive it must share the same durability guarantee.** The saga state was already persisted in `checkoutdb`; the timeout was not. v8b closes that gap so the *whole* workflow — state and timers — survives a process restart together.

---

## The Fix

Switch Quartz from `RAMJobStore` to the Postgres ADO store, backed by `checkoutdb`.

### 1. Package

Added `Quartz.Serialization.SystemTextJson` (3.15.0) to `SimpleStore.Checkout.API`. A persistent Quartz store requires a serializer; System.Text.Json keeps the service consistent with the rest of the .NET 10 codebase.

### 2. Quartz configuration (`Program.cs`)

```csharp
var checkoutDbConnectionString = builder.Configuration.GetConnectionString("checkoutdb")
    ?? throw new InvalidOperationException("Connection string 'checkoutdb' is required for the Quartz persistent store.");

builder.Services.AddQuartz(q =>
{
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;          // store MassTransit's payload as string job-data entries
        s.UsePostgres(pg =>
        {
            pg.ConnectionString = checkoutDbConnectionString;
            pg.TablePrefix = "qrtz_";    // lowercase to match the migration's tables
        });
        s.UseSystemTextJsonSerializer();
    });
});
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
```

Notes:
- **`UseProperties = true`** — MassTransit's scheduling job stores its message payload as string entries in the Quartz job-data map, so this avoids arbitrary-object serialization there and is the recommended setting for MassTransit + Quartz persistence.
- **`TablePrefix = "qrtz_"`** — lowercase matches the tables created by the migration and is robust regardless of whether the ADO delegate quotes identifiers (Postgres folds unquoted identifiers to lowercase).
- The MassTransit bus wiring is unchanged: `x.AddQuartzConsumers()` / `x.AddPublishMessageScheduler()` and `cfg.UsePublishMessageScheduler()` still drive the saga's `Schedule(...)` call — only the *store behind* Quartz changed.

### 3. Schema migration (`AddQuartzTables`)

The `qrtz_*` tables are owned by Quartz, not by EF model classes, so the DDL is applied as **raw SQL** in the migration (mirrors the official Quartz.NET `tables_postgres.sql`, lowercase names, idempotent with `IF NOT EXISTS`). It creates the standard Quartz schema: `qrtz_job_details`, `qrtz_triggers`, `qrtz_simple_triggers`, `qrtz_cron_triggers`, `qrtz_simprop_triggers`, `qrtz_blob_triggers`, `qrtz_calendars`, `qrtz_paused_trigger_grps`, `qrtz_fired_triggers`, `qrtz_scheduler_state`, `qrtz_locks`, plus their indexes.

**Ordering guarantee:** `Program.cs` runs `await db.Database.MigrateAsync()` *before* `app.Run()`. Quartz's hosted service only starts at `app.Run()`, so the `qrtz_*` tables always exist before Quartz first connects.

### 4. Design-time factory (`CheckoutDbContextFactory.cs`, new)

Adding the connection-string guard to `Program.cs` broke `dotnet ef migrations add`: EF design-time tooling builds the host, which now throws when the Aspire-injected `checkoutdb` connection string is absent at design time. The fix is a small `IDesignTimeDbContextFactory<CheckoutDbContext>` that constructs the context with a placeholder connection string, so EF tooling bypasses host building entirely. Migrations are still applied at runtime via `MigrateAsync()`.

---

## How It Resolves the Problem

Because the trigger row is now a row in `qrtz_triggers` (Postgres), it is part of the same durable store as the saga state:

1. Saga enters `AwaitingStock` → a one-shot trigger with `next_fire_time = now + 30s` is written to `qrtz_triggers`.
2. Checkout.API restarts (the in-memory scheduler would have lost the timer here).
3. On boot, Quartz's `AddQuartzHostedService` initializes against the persistent store, reloads all pending triggers, and **applies the misfire policy** to any whose `next_fire_time` already passed while the process was down — firing them immediately.
4. The `ReservationTimeoutExpired` message is delivered, the saga transitions `AwaitingStock → Cancelled`, and `OrderCancelledEvent` is published.

The stranded-order scenario is gone: a saga left in `AwaitingStock` across a restart is still cancelled.

---

## Operational Note: Multiple Replicas Need Clustering

The persistent store survives restarts for a **single Checkout.API replica** — which is what Aspire runs by default, and is consistent with the single-replica Inventory projector.

If you scale Checkout.API to **two or more replicas**, they all share the same `qrtz_*` tables, and a persistent store alone is *not* safe. Quartz's default mode is **non-clustered**, meaning each instance assumes it exclusively owns the tables. Two non-clustered instances against one store will double-fire triggers and corrupt each other's trigger state (lost/stuck timeouts) — this is explicitly unsupported.

To run multiple replicas you must additionally enable **clustering**:

```csharp
builder.Services.AddQuartz(q =>
{
    q.SchedulerId = "AUTO";          // unique instance id per replica
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;
        s.UseClustering(c =>         // DB-lock coordination + failover recovery
        {
            c.CheckinInterval = TimeSpan.FromSeconds(10);
            c.CheckinMisfireThreshold = TimeSpan.FromSeconds(20);
        });
        s.UsePostgres(pg => { pg.ConnectionString = checkoutDbConnectionString; pg.TablePrefix = "qrtz_"; });
        s.UseSystemTextJsonSerializer();
    });
});
```

- **`UseClustering()`** makes every node take a `SELECT … FOR UPDATE` row lock on `qrtz_locks` (`TRIGGER_ACCESS`) before acquiring triggers, so exactly one node fires each trigger. It also enables failover: nodes heartbeat into `qrtz_scheduler_state`, and a live node recovers the in-flight triggers (`qrtz_fired_triggers`) of a node whose heartbeat goes stale.
- **`SchedulerId = "AUTO"`** gives each replica a **unique instance id**. The cluster shares one scheduler *name* (how nodes find the same store) but each node needs a distinct *id* (how the cluster tells nodes apart, for heartbeats and crash recovery). Duplicate ids break failover detection.

No schema change is needed for clustering — `qrtz_locks`, `qrtz_scheduler_state`, and `qrtz_fired_triggers` (already created by `AddQuartzTables`) are the tables it uses. Clustering is left **off** here because Checkout runs a single replica and the per-cycle lock acquisition adds a (small) DB round-trip we don't need yet. Turn it on the day you run two replicas.

---

## Files Changed

| File | Change |
|------|--------|
| `src/SimpleStore.Checkout.API/SimpleStore.Checkout.API.csproj` | Add `Quartz.Serialization.SystemTextJson` 3.15.0 |
| `src/SimpleStore.Checkout.API/Program.cs` | `AddQuartz` → `UsePersistentStore(UsePostgres + UseSystemTextJsonSerializer)`; resolve `checkoutdb` connection string |
| `src/SimpleStore.Checkout.API/Data/CheckoutDbContextFactory.cs` *(new)* | Design-time `IDesignTimeDbContextFactory` for EF tooling |
| `src/SimpleStore.Checkout.API/Migrations/*_AddQuartzTables.cs` *(new)* | Raw Postgres DDL for the `qrtz_*` schema |
| `src/SimpleStore.Checkout.API/Sagas/CheckoutSagaStateMachine.cs` | Class comment updated: timeouts now durable |
| `docs/checkout-saga.md` | §11.2 rewritten to describe the persistent store |
| `CLAUDE.md` | Changelog + Checkout.API description updated |

---

## Verification

1. **Build:** `dotnet build SimpleStore.slnx` — 0 errors, 0 warnings.
2. **Migration registered:** `dotnet ef migrations list --project src/SimpleStore.Checkout.API --context CheckoutDbContext` lists `AddQuartzTables` (the design-time factory resolves without a live DB).
3. **End-to-end restart test (requires the running stack):**
   - `dotnet run --project src/SimpleStore.AppHost`
   - Submit an order, then **stop Inventory.API** so no reservation result is published.
   - While the saga is in `AwaitingStock` (check `checkout_saga_state` in PgWeb), **restart Checkout.API**.
   - Confirm a `qrtz_triggers` row exists for the saga, and that within ~30s of restart the order flips to `Cancelled` (reason `ReservationTimeout`) — proving the timer survived the restart.
