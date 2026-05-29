# v10 Changes — Observability Pass

## Overview

[v9](v9-changes.md) hardened every transient-failure path (EF retries, MassTransit retries, circuit breakers, KurrentDB reconnect loop, startup migration retry, single-flight token refresh). The system became resilient — but **invisible**. The only OpenTelemetry instrumentation in v9 was `AspNetCore` + `HttpClient` + `Runtime` on metrics, and `AspNetCore` + `HttpClient` on traces. The four heavy IO surfaces — EF Core, gRPC (KurrentDB), Redis, MassTransit — were entirely uninstrumented. There was no custom `ActivitySource` or `Meter` anywhere. The saga state machine logged nothing on transitions. Health-check tagging was muddled. The Gateway had no awareness of backend liveness.

v10 turns the lights on. Where v9 prevented cascading failures, v10 makes them queryable. Same shape: cross-cutting pass, no new features, no event-contract changes, no schema changes.

---

## 1. OpenTelemetry instrumentation packages + sampler knob

**Files:**
- `src/SimpleStore.ServiceDefaults/SimpleStore.ServiceDefaults.csproj`
- `src/SimpleStore.ServiceDefaults/Extensions.cs`
- `src/SimpleStore.Cart.API/Program.cs` *(Redis-specific late binding)*

**Problem:** of the four heavy IO surfaces in this system, **none** was instrumented. The trace stream contained inbound HTTP server spans + outbound HTTP client spans and nothing else — the message bus, the SQL it issued, the gRPC it spoke to KurrentDB, and the Redis commands Cart depended on were all dark.

**Fix:** added four instrumentation packages and wired them in `ConfigureOpenTelemetry`. The packages live in `ServiceDefaults` so every service gets them for free.

```xml
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.12.0-beta.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.GrpcNetClient"        Version="1.12.0-beta.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.StackExchangeRedis"   Version="1.12.0-beta.1" />
```

```csharp
metrics
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddMeter("MassTransit")      // built-in meter ships in the MassTransit package
    .AddMeter("SimpleStore.*");   // wildcard picks up every per-service Telemetry.Meter (§4)

tracing
    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(samplerArg)))
    .AddSource(builder.Environment.ApplicationName)
    .AddSource("MassTransit")
    .AddSource("SimpleStore.*")
    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => /* /health /alive /ready */ )
    .AddHttpClientInstrumentation()
    .AddGrpcClientInstrumentation()
    .AddEntityFrameworkCoreInstrumentation(o => o.SetDbStatementForText = true);
```

`SetDbStatementForText = true` puts the actual SQL text in the span's `db.statement` tag. Safe in SimpleStore — passwords are hashed in `IdentityService` before EF sees them, and the hot read paths use `AsNoTracking` reads that don't take user-supplied values inside `WHERE` clauses.

**Sampler:** `ParentBased(TraceIdRatioBased)`. `samplerArg = Configuration.GetValue<double?>("OTEL_TRACES_SAMPLER_ARG") ?? 1.0` — defaults to AlwaysOn (fine for the learning project so every trace shows up in the Aspire dashboard); production can dial down by setting the env var to `0.1` for 10% sampling. ParentBased keeps a trace whole — once the head sampling decision is made, every child span inherits it instead of each service re-deciding and producing fractured traces.

**Redis instrumentation** is the one that cannot live in `ServiceDefaults` because it needs the `IConnectionMultiplexer` Aspire registers — and only Cart.API has one. So Cart.API does the late-binding hook itself:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddRedisInstrumentation());
builder.Services.ConfigureOpenTelemetryTracerProvider((sp, _) =>
{
    var inst = sp.GetRequiredService<StackExchangeRedisInstrumentation>();
    inst.AddConnection(sp.GetRequiredService<IConnectionMultiplexer>());
});
```

**Impact:** every v9 hardening pass is now observable.
- v9 §1's EF retry attempts show up as repeated SQL spans on the same logical operation.
- v9 §2's `IExecutionStrategy.ExecuteAsync` wraps replay visibly when the lambda throws.
- v9 §3's KurrentDB reconnect emits a gRPC span on each `SubscribeAllAsync` call with `exception.type` recorded on the failure span.

---

## 2. MassTransit tracing + metrics

**Files:** no per-service code change required — purely a §1 consequence.

**Problem:** every `UsingRabbitMq` block in v9 ended with `cfg.ConfigureEndpoints(ctx)` and no OTel hookup. The entire saga + event-driven backbone was opaque: a failed reservation, a retried publish, a circuit-broken consumer — all silent.

**Fix:** MassTransit 8+ ships with built-in OTel emission. Once §1 registered `AddSource("MassTransit")` and `AddMeter("MassTransit")` on the OTel pipeline, publish / send / consume / saga dispatch all became spans, and `messaging.consumer.duration` / `messaging.producer.duration` / retry-count meters started flowing.

**Impact:** the seven-step checkout saga (Order → Checkout saga → Inventory → projector → Checkout saga → Order) becomes a single connected trace in the dashboard. v9 §4's `UseMessageRetry` retries show up as multiple consume spans with the same `messaging.message.id`; circuit-breaker trips appear as span events with `otel.status_code=ERROR`.

---

## 3. KurrentDB projector activity + projector-lag gauge

**Files:**
- `src/SimpleStore.Inventory.API/Projections/InventoryProjectionService.cs`
- `src/SimpleStore.Inventory.API/Observability/Telemetry.cs`

**Problem:** even after §1's gRPC instrumentation, the projector's per-event apply (read-DB write + checkpoint upsert + integration-event publish inside one Postgres transaction) had no parent activity binding the children together. Subscription lag was unknowable.

**Fix — activity span:** wrapped `InventoryProjectionService.ApplyOneAsync` in an activity:

```csharp
using var activity = Telemetry.Source.StartActivity("InventoryProjector.Apply", ActivityKind.Consumer);
activity?.SetTag("inventory.event_type", envelope.DomainEvent?.GetType().Name);
activity?.SetTag("inventory.is_live", envelope.IsLive);
activity?.SetTag("inventory.stream", envelope.StreamName);
activity?.SetTag("inventory.position.commit", unchecked((long)pos.CommitPosition));
```

The MassTransit publish (called inside `InventoryProjector.Apply*Async` for `StockLevelChangedEvent` / `StockReservedEvent`) and the EF execution-strategy transaction now become nested children of one parent — one trace per projected event.

**Fix — projector-lag gauge:** an `ObservableGauge<long>` reads two in-memory cursors maintained by the projector:

```csharp
private long _lastSeenTailCommit;   // updated as envelopes arrive from the subscription
private long _lastAppliedCommit;    // updated after each successful Apply transaction

Telemetry.SetProjectorLagProvider(() => /* delta = tail - applied */);
```

Cheap (two volatile reads, no I/O), no extra round-trips to KurrentDB. The metric `simplestore.inventory.projector.lag` reports commit-log byte units (`unit: "bytes"`) — KurrentDB's `$all` is indexed by byte position, so this is what's natural to measure. For typical payload sizes the delta is monotonically related to "events behind" and the operational question — "is the projector keeping up?" — has the same answer either way.

**Impact:** the gauge is the operator's "is the read model fresh?" signal. v9 §3's reconnect-on-failure now also emits a span on each subscription drop (via §1's gRPC instrumentation), with the exception recorded in span attributes. The activity from this section makes the EF retry inside the projection transaction visible as a child span on retry.

---

## 4. Per-service `Telemetry` static class (ActivitySource + Meter convention)

**Files (new, one per service):**
- `src/SimpleStore.Order.API/Observability/Telemetry.cs`
- `src/SimpleStore.Catalog.API/Observability/Telemetry.cs`
- `src/SimpleStore.Cart.API/Observability/Telemetry.cs`
- `src/SimpleStore.Inventory.API/Observability/Telemetry.cs`
- `src/SimpleStore.Checkout.API/Observability/Telemetry.cs`
- `src/SimpleStore.Identity.API/Observability/Telemetry.cs`
- `src/SimpleStore.Web/Observability/Telemetry.cs`
- `src/SimpleStore.Admin/Observability/Telemetry.cs`

**Problem:** zero custom `ActivitySource` / `Meter` across the codebase. Questions like "how many reservations failed because of stock?" or "how often does the v9 token-refresh coordinator actually deduplicate?" could only be answered by tail-grepping logs.

**Fix:** every service gets a 10-line `internal static class Telemetry` that hosts the per-service `ActivitySource` and `Meter` plus any business instruments. Inline per-service, not a shared library — mirrors the `TokenRefreshCoordinator` duplication precedent in [v9 §8](v9-changes.md#8-single-flight-token-refresh) and the no-shared-app-services rule in [CLAUDE.md](../CLAUDE.md). The mechanical convention:

```csharp
internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Order";
    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> OrdersSubmitted =
        Meter.CreateCounter<long>("simplestore.orders.submitted", description: "…");
    // …
}
```

`ServiceDefaults` registers the source and meter via the wildcard `AddSource("SimpleStore.*") / AddMeter("SimpleStore.*")` (§1) — no per-service registration line. New services adopting the convention only need to drop in their `Telemetry.cs`.

**Standard metric set:**

| Metric | Type | Service | Hot site |
|---|---|---|---|
| `simplestore.orders.submitted` | Counter | Order | `OrderService.CreateOrderAsync` |
| `simplestore.orders.confirmed` | Counter | Order | `OrderConfirmedConsumer.Consume` |
| `simplestore.orders.cancelled` | Counter | Order | `OrderCancelledConsumer.Consume` (tag `reason`) |
| `simplestore.reservations.requested` | Counter | Inventory | `ReserveStockRequestedConsumer.Consume` |
| `simplestore.reservations.succeeded` | Counter | Inventory | `CreateReservationHandler.HandleAsync` (success branch) |
| `simplestore.reservations.failed` | Counter | Inventory | `CreateReservationHandler.HandleAsync` (rejection branch) |
| `simplestore.inventory.projector.lag` | Observable gauge | Inventory | `Telemetry.SetProjectorLagProvider` (§3) |
| `simplestore.cart.fanout.duration` | Histogram | Cart | `ProductUpdatedConsumer.Consume` |
| `simplestore.identity.token_refresh.coalesced` | Counter | Web + Admin | `TokenRefreshCoordinator.RefreshAsync` |

The `token_refresh.coalesced` counter is the operational signal for [v9 §8](v9-changes.md#8-single-flight-token-refresh)'s effectiveness — it increments on every cache **hit** (a concurrent caller joining an in-flight rotation). A counter > 0 means the coordinator deduplicated; 0 means no concurrency was observed.

---

## 5. Saga state-transition logging + activity tags

**File:** `src/SimpleStore.Checkout.API/Sagas/CheckoutSagaStateMachine.cs`

**Problem:** the saga had zero log statements and zero activity tags. State changes were inferred entirely from message flow. Confirmed by grep: no `LogInformation` / `BeginScope` / `ActivitySource` anywhere in `SimpleStore.Checkout.API`.

**Fix:** the state machine now takes an `ILogger<CheckoutSagaStateMachine>` in the constructor and a private `LogTransition` helper that runs on each of the four transitions:

```csharp
.Then(ctx =>
{
    ctx.Saga.OrderId = ctx.Message.OrderId;
    // …
    LogTransition(ctx.Saga, from: "Initial", to: "AwaitingStock", reason: null);
})
```

`LogTransition` writes one structured log line and stamps four tags on `Activity.Current` (the MassTransit consumer-dispatch span auto-emitted by §2):

```csharp
activity.SetTag("saga.correlation_id", saga.CorrelationId);
activity.SetTag("saga.order_id",       saga.OrderId);
activity.SetTag("saga.state.from",     from);
activity.SetTag("saga.state.to",       to);
if (reason is not null) activity.SetTag("saga.cancel_reason", reason);
```

**Why no nested activity span:** the MassTransit consumer span from §2 already exists — exactly one per saga transition. A second nested span would duplicate it. Tag enrichment on the existing span is cheaper and surfaces the business label without adding noise to the trace tree.

**Impact:** every saga transition is queryable two ways — by `CorrelationId` in the logs view, by `saga.state.to` in the trace view. v9 §4's saga retries become "multiple consumer spans for the same `CorrelationId`" with the new state tag, so the dashboard now visualizes retry behavior directly.

---

## 6. Structured-logging upgrades — correlation scope + `LoggerMessage` in hot paths

**Files:**
- `src/SimpleStore.Order.API/Services/OrderService.cs`
- `src/SimpleStore.Order.API/Consumers/OrderConfirmedConsumer.cs`
- `src/SimpleStore.Order.API/Consumers/OrderCancelledConsumer.cs`
- `src/SimpleStore.Catalog.API/Consumers/StockLevelChangedConsumer.cs`
- `src/SimpleStore.Cart.API/Consumers/ProductUpdatedConsumer.cs` *(LoggerMessage)*
- `src/SimpleStore.Inventory.API/Consumers/ReserveStockRequestedConsumer.cs` *(LoggerMessage)*
- `src/SimpleStore.Inventory.API/Projections/InventoryProjector.cs` *(LoggerMessage on 3 apply methods)*
- `src/SimpleStore.Inventory.API/Application/Reservations/CreateReservationHandler.cs` *(counter increments)*

**Problem:** service methods on the happy path (`CreateOrderAsync`, `UpdateProductAsync`, `ApplyStockReservedAsync`) logged nothing — successful traces had no log entries to join. Existing consumer logs used structured placeholders correctly but did not open a `CorrelationId` scope, so the v8 saga key couldn't filter the log stream end-to-end.

**Fix — correlation scope:** every consumer that receives a message carrying a `CorrelationId` opens a scope at the top of `Consume`:

```csharp
using var _ = _log.BeginScope(new Dictionary<string, object>
{
    ["CorrelationId"] = msg.CorrelationId
});
```

`Extensions.cs` already sets `IncludeScopes = true` (v9), so the scope properties flow into both `ILogger`'s console output and the OTLP log records. The Aspire dashboard's log view filters on `CorrelationId` for free. `OrderService.CreateOrderAsync` opens the same scope after minting the new `CorrelationId`, so every log line that follows — including any EF Core retry log inside the execution strategy — carries the key.

For consumers whose event has no saga `CorrelationId` (Catalog's `StockLevelChangedConsumer`, Cart's `ProductUpdatedConsumer`), the scope uses `ProductId` instead so the dashboard can collate every cart-refresh attempt or stock-update for one product.

**Fix — `LoggerMessage` source generation** in 3–4 hot-path methods only:
- `InventoryProjector.ApplyDeliveryNoteIssuedAsync` / `ApplyReceiptNoteRecordedAsync` / `ApplyStockReservedAsync` (called per event during projection — including cold-start replay)
- `ProductUpdatedConsumer.Consume` (called per product update + SCAN fan-out across every cart key)
- `ReserveStockRequestedConsumer.Consume` (called per checkout)

Each converts to a `static partial void Log…` with `[LoggerMessage]`:

```csharp
[LoggerMessage(EventId = 1301, Level = LogLevel.Information,
    Message = "Projector applied DeliveryNoteIssuedV1 {NoteId} ({LineCount} lines, isLive={IsLive}).")]
private static partial void LogApplyDelivery(ILogger logger, Guid noteId, int lineCount, bool isLive);
```

The formatter is generated at compile time and the call site uses no boxing and no reflection. Three or four methods only — **not** a blanket rollout. Classic `ILogger.LogInformation` remains the convention everywhere else.

**No-PII rule preserved:** passwords, refresh-token values, and email addresses still never enter a log statement (verified by grep during the v10 exploration pass).

---

## 7. Health-check tagging — `"ready"` tag + `/ready` endpoint

**Files:**
- `src/SimpleStore.ServiceDefaults/Extensions.cs`
- `src/SimpleStore.Inventory.API/Program.cs` *(KurrentDB check registration)*

**Problem:** [v9 §5](v9-changes.md#5-health-check-dependency-probes) mapped `/health` to run **every** registered check — including the trivial `self` liveness check that should never gate readiness. The semantics happened to be correct (any-down → 503) but the tag dimension was muddled: there was no `"ready"` tag, so adding more liveness-only checks later would silently change `/health` behavior.

**Fix — `/ready` endpoint:**

```csharp
app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready")
});
```

**Fix — auto-tag Aspire-registered probes:** Aspire's `AddNpgsqlDbContext` / `AddRedisDistributedCache` / `AddRedisClient` components and MassTransit register their dependency checks without a `"ready"` tag, so a post-build configurator adds it:

```csharp
builder.Services.PostConfigure<HealthCheckServiceOptions>(o =>
{
    foreach (var r in o.Registrations)
        if (AspireDependencyCheckPrefixes.Any(p =>
                r.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            r.Tags.Add("ready");
});
```

The KurrentDB check in Inventory.API is registered with the tag explicitly:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<KurrentDbHealthCheck>("kurrentdb", tags: ["ready"]);
```

**Endpoint semantics now:**
- `/alive` — `"live"`-tagged only (just the trivial self-check). Used for k8s **liveness** — restart if not 200.
- `/ready` — `"ready"`-tagged only (every dependency probe). Used for k8s **readiness** — stop routing traffic but do NOT restart.
- `/health` — every registered check (aggregate). Kept for backward compatibility with Aspire dashboards and v9 consumers (e.g. §8's YARP probes).

**Impact:** clean separation of liveness / readiness / aggregate. Future custom liveness-only checks won't silently affect readiness because they won't carry the `"ready"` tag.

---

## 8. Gateway active health-check probe (YARP)

**File:** `src/SimpleStore.Gateway/appsettings.json`

**Problem:** YARP routed JWT-protected traffic to all five backends with no awareness of whether they were up. If `identity` was down, every request through the gateway returned a confusing 401 (token validation failed because the issuer's keys were unreachable) instead of a clean 503. The gateway had no signal it could use — until v9 §5 made `/health` reliable in every environment, the backend's own state was opaque from the edge. v10 finally consumes that signal.

**Fix:** turned on YARP's `ActiveHealthCheck` on every cluster:

```json
"identity-cluster": {
  "HealthCheck": {
    "Active": {
      "Enabled":  true,
      "Interval": "00:00:10",
      "Timeout":  "00:00:03",
      "Policy":   "ConsecutiveFailures",
      "Path":     "/health"
    }
  },
  "Metadata": { "ConsecutiveFailuresHealthPolicy.Threshold": "1" },
  "Destinations": { "primary": { "Address": "https+http://identity" } }
}
```

YARP destinations flip to unhealthy when `/health` returns 503 (the dependency-aggregate endpoint, kept around in §7) and YARP serves 503 directly to the client without dispatching. The `identity` cluster gets the stricter `Threshold: 1` so a single failed probe sidelines it — Identity is the cross-cutting auth dependency, and once it's down every authenticated route through the gateway is going to fail anyway. The other four clusters get YARP's default threshold (5 consecutive failures, ~50 s of failure before flipping).

No code change in `Gateway/Program.cs` — YARP picks up the config block automatically once `AddReverseProxy().LoadFromConfig(...)` is in place (which it has been since v5).

**Impact:** [v9 §5](v9-changes.md#5-health-check-dependency-probes) made `/health` reliable in all environments. v10 §8 finally consumes it from the edge — a downed Identity now produces a clean 503 to the client instead of a confusing 401.

---

## 9. New / changed files at a glance

| Path | Change |
|---|---|
| `src/SimpleStore.ServiceDefaults/SimpleStore.ServiceDefaults.csproj` | +3 OTel instrumentation packages |
| `src/SimpleStore.ServiceDefaults/Extensions.cs` | EF / gRPC / MT instrumentation, sampler knob, `/ready` endpoint, auto-tag helper |
| `src/SimpleStore.Order.API/Observability/Telemetry.cs` | **new** |
| `src/SimpleStore.Order.API/Services/OrderService.cs` | correlation scope + `OrdersSubmitted` counter |
| `src/SimpleStore.Order.API/Consumers/OrderConfirmedConsumer.cs` | scope + `OrdersConfirmed` counter |
| `src/SimpleStore.Order.API/Consumers/OrderCancelledConsumer.cs` | scope + `OrdersCancelled` counter (tag `reason`) |
| `src/SimpleStore.Catalog.API/Observability/Telemetry.cs` | **new** |
| `src/SimpleStore.Catalog.API/Consumers/StockLevelChangedConsumer.cs` | scope |
| `src/SimpleStore.Cart.API/Observability/Telemetry.cs` | **new** (incl. `CartFanoutDuration` histogram) |
| `src/SimpleStore.Cart.API/Program.cs` | Redis OTel instrumentation late binding |
| `src/SimpleStore.Cart.API/Consumers/ProductUpdatedConsumer.cs` | `LoggerMessage` + scope + fan-out histogram |
| `src/SimpleStore.Inventory.API/Observability/Telemetry.cs` | **new** (incl. `ProjectorLag` observable gauge) |
| `src/SimpleStore.Inventory.API/Projections/InventoryProjectionService.cs` | activity per event + lag-cursor wiring |
| `src/SimpleStore.Inventory.API/Projections/InventoryProjector.cs` | `LoggerMessage` on the 3 apply methods |
| `src/SimpleStore.Inventory.API/Consumers/ReserveStockRequestedConsumer.cs` | `LoggerMessage` + scope + `ReservationsRequested` counter |
| `src/SimpleStore.Inventory.API/Application/Reservations/CreateReservationHandler.cs` | `ReservationsSucceeded` / `ReservationsFailed` counters |
| `src/SimpleStore.Inventory.API/Program.cs` | KurrentDB check tagged `"ready"` |
| `src/SimpleStore.Checkout.API/Observability/Telemetry.cs` | **new** |
| `src/SimpleStore.Checkout.API/Sagas/CheckoutSagaStateMachine.cs` | per-transition log + activity tags |
| `src/SimpleStore.Identity.API/Observability/Telemetry.cs` | **new** |
| `src/SimpleStore.Web/Observability/Telemetry.cs` | **new** (incl. `TokenRefreshCoalesced` counter) |
| `src/SimpleStore.Web/Services/Auth/TokenRefreshCoordinator.cs` | coalescing-hit counter |
| `src/SimpleStore.Admin/Observability/Telemetry.cs` | **new** (incl. `TokenRefreshCoalesced` counter) |
| `src/SimpleStore.Admin/Services/Auth/TokenRefreshCoordinator.cs` | coalescing-hit counter |
| `src/SimpleStore.Gateway/appsettings.json` | YARP active health check per cluster |

---

## Out of scope (deferred)

- **Frontend (browser-side) telemetry** for Web (MVC) and Admin (Blazor Server). No JS SDK, no Blazor circuit telemetry. Server-side traces already cover all cross-service hops; browser is a separate skill set.
- **OpenTelemetry log shipping to an external store** (Loki / Seq / Elasticsearch). OTLP export is on (v9 + v10 §1); Aspire dashboard renders logs / traces / metrics locally. Queryable external log store is out of scope for a learning project.
- **Custom span exemplars on metrics.** Aspire dashboard doesn't render exemplars; not worth the wiring cost.
- **Distributed tracing for `BearerTokenHandler` refresh coordination internals.** v9 §8's coordinator is correct; instrumenting the coalescing key would leak a refresh-token hash into a span tag. The `token_refresh.coalesced` counter (v10 §4) measures effectiveness without that risk.
- **Production health-endpoint auth gating.** Deferred from v9; still deferred — observability hardening shouldn't introduce a shared health-token mechanism.
- **YARP per-cluster retry / circuit-breaker policy.** Deferred from v9; still deferred (HTTP client `AddStandardResilienceHandler` already protects outbound calls).
- **Multi-replica Inventory projector via KurrentDB persistent subscriptions.** Deferred from v9; v10 makes the single-replica projector observable but doesn't change its replication model.

---

## Verification

[v9's verification](v9-changes.md#verification) checklist was functional ("did the system recover?"). v10's is **visual** ("can you see what happened in the Aspire dashboard?"):

1. **Build:** `dotnet build SimpleStore.slnx` — all projects compile.
2. **Boot:** `dotnet run --project src/SimpleStore.AppHost` and open the Aspire dashboard.
3. **End-to-end trace:** sign in as `demo@simplestore.local`, add a product to the cart, check out. In the dashboard's Traces view, find the `POST /api/order/orders` trace and confirm the span tree contains:
   - HTTP server span → EF `INSERT Order` span (with full SQL in the `db.statement` tag)
   - MassTransit `publish OrderSubmittedEvent` span
   - Checkout saga `consume` span (tag `saga.state.to=AwaitingStock`)
   - MassTransit `publish ReserveStockRequestedEvent` span
   - Inventory `consume` span → KurrentDB gRPC `AppendAsync` span
   - `InventoryProjector.Apply` activity (tag `inventory.event_type=StockReservedV1`)
   - MassTransit `publish StockReservedEvent` span
   - Checkout saga `consume` span (tag `saga.state.to=Confirmed`)
   - MassTransit `publish OrderConfirmedEvent` span
   - Order `consume` span. The entire saga is one trace.
4. **Metrics view:** in the dashboard's Metrics panel, confirm `simplestore.orders.submitted`, `simplestore.orders.confirmed`, `simplestore.reservations.requested`, `simplestore.reservations.succeeded` all tick up by 1 after each checkout. Watch `simplestore.inventory.projector.lag` hover near 0 during steady state.
5. **Failure visibility (v9 retries):** stop Postgres from the Aspire dashboard mid-checkout. Watch the EF retry spans appear as siblings under the same logical operation (v9 §1 + §2 now visible). Watch the MassTransit consume span retry up to 5× (v9 §4 now visible). Confirm `messaging.consumer.duration` histogram shows the failed attempts. Restart Postgres; the system recovers.
6. **Saga-transition visibility:** at the saga consume span, confirm `saga.state.from`, `saga.state.to`, `saga.correlation_id`, and `saga.order_id` tags are set. Filter the logs view by `CorrelationId` and confirm the four transition log lines appear in order.
7. **Health endpoints:** hit `/alive` (200), `/ready` (200), `/health` (200) on each service. Stop `rabbitmq` from the Aspire dashboard; watch `/ready` and `/health` flip to 503 on services that depend on the bus while `/alive` stays 200. Stop `identity` and confirm the Gateway's authenticated routes return 503 (via §8's YARP active probe) instead of 401 — and that the gateway's own `/ready` flips when `identity-cluster` has no healthy destinations.
8. **Token-refresh coalescing:** open the storefront in a private window, log in, let the access token expire (default 15 min, or set `Jwt:AccessTokenLifetime` shorter for faster testing), then reload the page. Confirm `simplestore.identity.token_refresh.coalesced` increments by N−1 (where N is the number of concurrent outbound API calls fanned out at page load) — visual proof of v9 §8's effectiveness.
9. **Sampler knob:** set `OTEL_TRACES_SAMPLER_ARG=0.1` in the AppHost (env var); restart; verify roughly 10% of traces appear in the dashboard. Set back to `1.0` for normal development.
