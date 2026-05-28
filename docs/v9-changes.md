# v9 Changes — Resilience Pass

## Overview

Before v9, the only resilience in SimpleStore was `AddStandardResilienceHandler()` on outbound HTTP clients (from `ServiceDefaults`) and MassTransit's transactional outbox/inbox. Every other dependency — Postgres (EF Core), Redis, RabbitMQ consumers, KurrentDB — ran with SDK defaults. A single transient blip in any of them surfaced as a 500 to the user, a stuck saga, or a stalled projector.

v9 hardens the system so transient infra failures **degrade gracefully** instead of cascading. No new features, no event-contract changes, no read-model schema changes.

---

## 1. EF Core retry-on-failure (5 DbContexts)

**Files:** `Program.cs` in `Identity.API`, `Catalog.API`, `Order.API`, `Inventory.API`, `Checkout.API`.

**Problem:** every `AddNpgsqlDbContext<…>("…")` call accepted the bare default — no retry on transient Postgres errors (failover, restart, brief network glitch, idle-connection drop behind a load balancer). A single hiccup propagated to the caller.

**Fix:** switched each call to the configurator overload, disabled Aspire's built-in simple retry (`settings.DisableRetry = true`), and enabled an explicit `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 10s)` so EF Core's own execution strategy handles transient Npgsql errors. `CommandTimeout` is set to 30 s.

```csharp
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb",
    configureSettings: settings =>
    {
        settings.DisableRetry = true;
        settings.CommandTimeout = 30;
    },
    configureDbContextOptions: opt =>
        opt.UseNpgsql(npgsql =>
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null)));
```

---

## 2. `IExecutionStrategy` wrapping (4 transaction sites)

**Files:**
- `src/SimpleStore.Order.API/Services/OrderService.cs` — `CreateOrderAsync`
- `src/SimpleStore.Catalog.API/Services/CatalogService.cs` — `UpdateProductAsync`
- `src/SimpleStore.Inventory.API/Application/Reservations/CreateReservationHandler.cs` — `HandleAsync`
- `src/SimpleStore.Inventory.API/Projections/InventoryProjectionService.cs` — `ApplyOneAsync` + `CheckpointOnlyAsync`

**Problem:** once `EnableRetryOnFailure` is on, EF Core forbids user-initiated transactions (`BeginTransactionAsync`) outside an execution strategy — calls would throw `InvalidOperationException`.

**Fix:** wrap each multi-step transaction in `db.Database.CreateExecutionStrategy().ExecuteAsync(async () => { ... })`. On a transient error the entire unit of work replays:

```csharp
var strategy = _context.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var tx = await _context.Database.BeginTransactionAsync(ct);
    // ... existing work ...
    await tx.CommitAsync(ct);
});
```

**Reservation handler caveat:** the `SELECT … FOR UPDATE` lock is re-acquired on every attempt, and the KurrentDB `AppendAsync` inside is idempotent because the deterministic `ReservationId` collapses retries onto the same stream — the existing `ConcurrencyConflictException` branch (added in v8) already treats that as a no-op success.

---

## 3. KurrentDB subscription auto-reconnect (Inventory projector)

**File:** `src/SimpleStore.Inventory.API/Projections/InventoryProjectionService.cs`

**Problem:** the projector's `await foreach` over `IEventStore.SubscribeAllAsync` exited on the first transient KurrentDB error (network drop, gRPC deadline, server restart). The projection background service stopped, the read model went stale, and no recovery happened until the whole API was restarted.

**Fix:** the body of `ExecuteAsync` is wrapped in an outer reconnect loop with exponential backoff (1 s → 30 s). On any exception (except cancellation) the projector logs the drop, sleeps the current backoff, and re-enters the loop. `RunSubscriptionLoopAsync` reloads the checkpoint from Postgres at the top of each iteration so the subscription resumes from the exact `(commit, prepare)` position the last successful projection committed.

Per-event idempotency was already guaranteed by `InventoryProjector`'s per-aggregate "have I seen this Id" guards, so re-receiving the last in-flight event on reconnect is a safe no-op. The per-event apply is now also wrapped in `IExecutionStrategy` (see §2) so a transient Postgres failure on the read side retries the projection transaction without dropping the subscription.

---

## 4. MassTransit consumer resilience (5 services)

**Files:** `Program.cs` in `Order.API`, `Catalog.API`, `Cart.API`, `Inventory.API`, `Checkout.API`.

**Problem:** every `UsingRabbitMq` config block ended with `cfg.ConfigureEndpoints(ctx)` and nothing else. A consumer that threw once (DB blip, downstream timeout, transient Redis hiccup) went straight to the `_skipped` / `_error` queue. No retry, no backoff, no circuit breaker.

**Fix:** three filters added to every endpoint, plus a RabbitMQ heartbeat:

```csharp
cfg.Host(new Uri(connStr), h =>
{
    h.Heartbeat(TimeSpan.FromSeconds(30));
    h.RequestedConnectionTimeout(TimeSpan.FromSeconds(10));
});

cfg.UseMessageRetry(r => r.Exponential(
    retryLimit: 5,
    minInterval: TimeSpan.FromSeconds(1),
    maxInterval: TimeSpan.FromSeconds(30),
    intervalDelta: TimeSpan.FromSeconds(2)));

cfg.UseCircuitBreaker(cb =>
{
    cb.TrackingPeriod  = TimeSpan.FromMinutes(1);
    cb.TripThreshold   = 15;       // % failures
    cb.ActiveThreshold = 10;       // minimum messages in window
    cb.ResetInterval   = TimeSpan.FromMinutes(5);
});
```

- **Retry** absorbs transient failures (up to 5 attempts, exponential backoff with jitter).
- **Circuit breaker** trips when ≥ 15 % of the last ≥ 10 messages fail and stops dispatching for 5 minutes — prevents a degraded downstream from being hammered to death.
- **Heartbeat + connection timeout** keep long-lived AMQP connections alive across NAT/LB idle timeouts. Automatic-recovery and topology-recovery were already on by default in `RabbitMQ.Client`.

**Delayed redelivery deliberately omitted in v9** — would require either the RabbitMQ delayed-exchange plugin (AppHost change) or an in-memory scheduler (not durable across restarts). The 5-attempt exponential retry already covers transient blips; longer outages flow to the `_error` queue for operator replay.

**Saga note:** MassTransit applies `UseMessageRetry` to saga consumers automatically. The Checkout saga's pessimistic-concurrency row lock (existing v8) serializes concurrent dispatches per saga, so retries cannot race with each other.

---

## 5. Health-check dependency probes

**Files:**
- `src/SimpleStore.ServiceDefaults/Extensions.cs` — endpoint gating removed
- `src/SimpleStore.Inventory.API/Infrastructure/KurrentDbHealthCheck.cs` *(new)*
- `src/SimpleStore.Inventory.API/Program.cs` — register the KurrentDB check

**Problem:** every service exposed only `AddCheck("self", () => Healthy(), ["live"])`, and the `/health` and `/alive` endpoints were mapped **only in `IsDevelopment()`**. There was no way for Aspire / k8s probes in any other environment to tell a healthy service from a broken one.

**Fix — endpoint exposure:** moved `MapHealthChecks` calls out of the dev-only block in `ServiceDefaults`. Endpoints now respond in every environment. The probes only leak per-dependency up/down state (no PII, no stack traces); auth-gating is deferred to v10.

**Fix — dependency probes:** Aspire already auto-registers a Postgres health check from `AddNpgsqlDbContext` and a Redis health check from `AddRedisDistributedCache` / `AddRedisClient`; MassTransit auto-registers a `masstransit-bus` check from `AddMassTransit`. All three now show up on `/health` for free.

KurrentDB has no Aspire wrapper, so we wrote a small `KurrentDbHealthCheck` (~50 lines) that calls `ReadAllAsync(Direction.Backwards, Position.End, maxCount: 1)` with a 3-second timeout. `ReadState.Ok` and `ReadState.StreamNotFound` both indicate a healthy connection.

**Endpoint semantics:**
- `/alive` — runs only `"live"`-tagged checks (just the trivial self-check). Returns 200 as long as the process is responsive. Used for k8s **liveness**.
- `/health` — runs every registered check. Returns 503 when any dependency is down. Used for k8s **readiness** — stop routing traffic, but don't kill the container.

---

## 6. Startup migration retry

**Files:**
- `src/SimpleStore.ServiceDefaults/StartupMigrationRunner.cs` *(new)*
- `Program.cs` in `Identity.API`, `Catalog.API`, `Order.API`, `Inventory.API`, `Checkout.API` — migrate/seed block

**Problem:** every service's `Program.cs` called `MigrateAsync()` (plus a seeder) inside a synchronous `using var scope = …` block. If Postgres was briefly unreachable at boot (rolling restart, slow container start) the exception propagated and the service crash-looped — Aspire's `WaitFor(postgres)` triggers on container "running", not necessarily on the database accepting connections.

**Fix:** introduced `SimpleStore.ServiceDefaults.StartupMigrationRunner.RunAsync(app, migrateFn)` — a 5-attempt bounded retry with exponential backoff (1 s → 16 s). Each service now calls:

```csharp
await StartupMigrationRunner.RunAsync(app, async (sp, _) =>
{
    var ctx = sp.GetRequiredService<OrderDbContext>();
    await OrderSeeder.SeedAsync(ctx);
});
```

If the migration still fails after 5 attempts, the original exception propagates and the service exits with a non-zero code — i.e. retries are bounded, not infinite.

---

## 7. Cart / Redis hardening

**Files:**
- `src/SimpleStore.Cart.API/Services/RedisCartStore.cs`
- `src/SimpleStore.Cart.API/Middleware/RedisExceptionMiddleware.cs` *(new)*
- `src/SimpleStore.Cart.API/Program.cs` — wires the middleware

**Problem:** `RedisCartStore` called `IDistributedCache` and `IConnectionMultiplexer` with no exception handling. A `RedisTimeoutException` or `RedisConnectionException` bubbled all the way out as a 500 on the storefront — even on `GET /api/cart` where degrading to an empty cart would be a perfectly acceptable user experience.

**Fix — read path:** added `TryLoadItemsAsync` that catches `RedisConnectionException` / `RedisTimeoutException`, logs a `Warning`, and returns an empty list. Only `GetAsync` uses it; read-modify-write paths (`AddItemAsync`, `UpdateItemAsync`, `RemoveItemAsync`, `MergeAsync`) intentionally keep using `LoadItemsAsync` (re-throws), because silently treating the cart as empty before a write would risk clobbering the user's saved cart on Redis recovery.

**Fix — write path:** the new `RedisExceptionMiddleware` catches `RedisConnectionException` / `RedisTimeoutException` at the HTTP boundary and returns a clean `503 Service Unavailable` with `Retry-After: 5` and a `application/problem+json` body instead of a 500. The storefront can present a "try again shortly" message instead of an error page.

**Fan-out consumer:** `ProductUpdatedConsumer` already had no try/catch — it propagates Redis exceptions to MassTransit, which now (see §4) retries up to 5 times with exponential backoff. The SCAN-based fan-out is idempotent (rewriting identical denormalized fields is a no-op), so restarting the scan on retry is safe.

---

## 8. Single-flight token refresh

**Files (per app):**
- `Services/Auth/TokenRefreshCoordinator.cs` *(new — in both Web and Admin)*
- `Services/Auth/BearerTokenHandler.cs` — updated
- `Program.cs` — registers `TokenRefreshCoordinator` as a singleton

**Problem:** `BearerTokenHandler` saw an expired access token on every concurrent outbound request and each call independently invoked `IIdentityApiClient.RefreshAsync`. With **N** concurrent requests (page-load fan-out: catalog + order count + cart in parallel) Identity got **N** refresh calls — a self-inflicted thundering herd. Worse: Identity rotates refresh tokens on use (v3+), so only one of the racing calls succeeds; the rest get **401 Unauthorized** and the outbound call falls through unauthenticated.

**Fix:** introduced `TokenRefreshCoordinator` — a singleton that keys a `ConcurrentDictionary<string, Lazy<Task<LoginResponse?>>>` by the current refresh-token value. The first caller installs the `Lazy` and triggers the network call; subsequent callers reuse the same `Task` and get the same rotated tokens back. On completion the dictionary entry is removed so a future expiry (with the new refresh token) starts a fresh coordination.

`BearerTokenHandler` was updated to:
1. Call `_coordinator.RefreshAsync(currentRefreshToken, () => _identity.RefreshAsync(...))` instead of `_identity.RefreshAsync(...)` directly.
2. **Not** pass the per-request `CancellationToken` into the shared rotation call — cancelling one racing caller must not abort the rotation the others are awaiting.
3. After the rotation returns, re-read the token store and only persist if our `RefreshToken` is still the current one (avoids a late writer clobbering an even-newer rotation).

The coordinator is duplicated across `SimpleStore.Web` and `SimpleStore.Admin` because there is no shared application-services project (per `CLAUDE.md` conventions).

---

## 9. New / changed files at a glance

| Path | Change |
|---|---|
| `src/SimpleStore.ServiceDefaults/Extensions.cs` | Health endpoints exposed in all environments |
| `src/SimpleStore.ServiceDefaults/StartupMigrationRunner.cs` | **new** |
| `src/SimpleStore.Identity.API/Program.cs` | EF retry + StartupMigrationRunner |
| `src/SimpleStore.Catalog.API/Program.cs` | EF retry + MassTransit retry/CB/heartbeat + StartupMigrationRunner |
| `src/SimpleStore.Catalog.API/Services/CatalogService.cs` | `IExecutionStrategy` wrap |
| `src/SimpleStore.Order.API/Program.cs` | EF retry + MassTransit retry/CB/heartbeat + StartupMigrationRunner |
| `src/SimpleStore.Order.API/Services/OrderService.cs` | `IExecutionStrategy` wrap |
| `src/SimpleStore.Cart.API/Program.cs` | MassTransit retry/CB/heartbeat + middleware wired |
| `src/SimpleStore.Cart.API/Services/RedisCartStore.cs` | `TryLoadItemsAsync` for safe reads |
| `src/SimpleStore.Cart.API/Middleware/RedisExceptionMiddleware.cs` | **new** |
| `src/SimpleStore.Inventory.API/Program.cs` | EF retry + MassTransit retry/CB/heartbeat + StartupMigrationRunner + KurrentDB health check |
| `src/SimpleStore.Inventory.API/Application/Reservations/CreateReservationHandler.cs` | `IExecutionStrategy` wrap |
| `src/SimpleStore.Inventory.API/Projections/InventoryProjectionService.cs` | reconnect loop + `IExecutionStrategy` wrap |
| `src/SimpleStore.Inventory.API/Infrastructure/KurrentDbHealthCheck.cs` | **new** |
| `src/SimpleStore.Checkout.API/Program.cs` | EF retry + MassTransit retry/CB/heartbeat + StartupMigrationRunner |
| `src/SimpleStore.Web/Program.cs` | Registers `TokenRefreshCoordinator` |
| `src/SimpleStore.Web/Services/Auth/BearerTokenHandler.cs` | Uses the coordinator |
| `src/SimpleStore.Web/Services/Auth/TokenRefreshCoordinator.cs` | **new** |
| `src/SimpleStore.Admin/Program.cs` | Registers `TokenRefreshCoordinator` |
| `src/SimpleStore.Admin/Services/Auth/BearerTokenHandler.cs` | Uses the coordinator |
| `src/SimpleStore.Admin/Services/Auth/TokenRefreshCoordinator.cs` | **new** |

---

## Out of scope (deferred)

- **YARP per-cluster retry/circuit-breaker policy.** The HTTP client `AddStandardResilienceHandler` already protects outbound calls; gateway-side policy is duplicate work. Defer to v10.
- **Quartz clustering / multi-replica Checkout.API.** Explicitly single-replica per v8b notes.
- **Saga stuck-state recovery (manual abort endpoint).** Needs a separate design pass.
- **Production health-endpoint authentication.** Endpoints currently leak only up/down state per dependency; auth-gating would mean a shared health token. Defer to v10.
- **KurrentDB persistent subscriptions (multi-replica projector).** v9 keeps the single-replica projector but makes it self-healing.
- **MassTransit delayed redelivery.** Requires Rabbit delayed-exchange plugin or in-memory scheduler. The 5-attempt exponential retry covers transient blips; longer outages flow to `_error` for operator replay.

---

## Verification

1. **Build:** `dotnet build SimpleStore.slnx` — all projects compile.
2. **Smoke test:** `dotnet run --project src/SimpleStore.AppHost`; sign in as `demo@simplestore.local`, add a product, check out; sign in as `admin@simplestore.local`, edit a product, watch the consumer log fire on Cart.
3. **Health endpoints:** hit each service's `/alive` (200) and `/health` (200). Stop a dependency from the Aspire dashboard and watch the relevant service's `/health` flip to 503 while `/alive` stays 200.
4. **Transient-failure simulation:** stop each of `postgres`, `kurrentdb`, `rabbitmq`, `cart-redis` in turn; verify the system recovers without an API restart once the dependency is back up. Inspect logs for the new "subscription dropped", "Startup migration attempt N failed", and "Redis unreachable while loading cart" structured warnings.
5. **Concurrent refresh:** open the storefront in a private window, log in, let the access token expire (default 15 min in dev, or set a shorter `Jwt:AccessTokenLifetime` to test faster), then reload the page. With `TokenRefreshCoordinator` only one `POST /api/identity/refresh` should fire in Identity's request log instead of one per outbound API client.
