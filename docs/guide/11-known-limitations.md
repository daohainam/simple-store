# Chapter 11 — Known Limitations: What This Project Teaches vs. What Production Needs

SimpleStore is a **teaching** project. It deliberately keeps some things simple so that the *main* ideas (service boundaries, events, sagas, event sourcing) stay visible. This chapter lists the places where the code takes a shortcut, why that is acceptable here, and what you would change in a real system.

Reading this chapter is a good exercise on its own: for each item, try to explain **what could go wrong** before you read the "production fix".

> **How to read this list.** Every item below was checked against the source code. Where a statement depends on the default behaviour of a library (MassTransit, YARP, ...) rather than on code in this repository, it is marked **"to verify"** — test it before you rely on it.

## What you will learn

- Which simplifications are intentional and which are real gaps.
- How each gap would show up in production.
- What the usual fix is, so you can extend the project as a learning exercise.

---

## 1. Consistency and concurrency

### 1.1 Stock can be oversold under heavy concurrency

- **Where:** [CreateReservationHandler.cs](../../src/SimpleStore.Inventory.API/Application/Reservations/CreateReservationHandler.cs)
- **What happens:** the handler locks the `stock_levels` rows with `SELECT ... FOR UPDATE` and checks availability. But it does **not** decrement `OnHand`. The decrement is done later, asynchronously, by the projector after the `StockReservedV1` event is read back from KurrentDB.
- **Failure scenario:** two reservations for the last unit arrive before the projector has applied the first one. Both read `OnHand = 1`, both pass the check, both append `StockReservedV1`. The projector then drives `OnHand` to `-1` (the column allows negative values).
- **Why it is acceptable here:** it keeps the write side purely event-sourced and demonstrates eventual consistency. The handler's header comment and [checkout-saga.md §10.3](../checkout-saga.md) document the race (and §13 sketches the per-product-stream fix).
- **Production fix:** make the reservation a decision made *inside the aggregate* using a stock-level stream (a per-product aggregate whose stream revision is the lock), or decrement a "available to promise" counter synchronously in the same transaction as the check.

### 1.2 Payment has no row-level concurrency control

- **Where:** [PaymentService.cs](../../src/SimpleStore.Payment.API/Services/PaymentService.cs), [PaymentDbContext.cs](../../src/SimpleStore.Payment.API/Data/PaymentDbContext.cs)
- **What happens:** `PaymentAccount` has no concurrency token and the debit does not lock the row. `ledger.OrderId` / `CorrelationId` have no unique index. "No double charge" relies **only** on the MassTransit inbox (which de-duplicates redelivered messages).
- **Failure scenario:** a customer deposits at the same moment a debit runs. Both read the same balance and one update overwrites the other (a classic lost update).
- **Production fix:** add a Postgres `xmin` concurrency token (or `SELECT ... FOR UPDATE`) on the account, and a unique index on `(AccountId, CorrelationId)` for `Payment` rows.

### 1.3 Cart merge is not atomic

- **Where:** `RedisCartStore.MergeAsync` in [RedisCartStore.cs](../../src/SimpleStore.Cart.API/Services/RedisCartStore.cs)
- **What happens:** it reads two carts, writes the destination, then deletes the source. Two concurrent merges (or a merge during an add) can lose an update.
- **Why acceptable:** the merge runs once per login; the window is tiny.
- **Production fix:** a Lua script or a Redis transaction (`WATCH`/`MULTI`), or store the cart as a Redis hash and use `HINCRBY` per line.

---

## 2. The checkout saga

### 2.1 Late, duplicate and out-of-order events are not handled explicitly

- **Where:** [CheckoutSagaStateMachine.cs](../../src/SimpleStore.Checkout.API/Sagas/CheckoutSagaStateMachine.cs)
- **What the code contains:** each state handles only the events it expects. There is no `Ignore(...)`, `DuringAny(...)`, `OnUnhandledEvent(...)` or `OnMissingInstance(...)` anywhere in the project.
- **What that means:** what happens when, for example, a `StockReserved` arrives while the saga is in `AwaitingPayment`, or any event arrives after the saga row has been deleted, is decided by MassTransit's defaults. *To verify:* check the behaviour of your MassTransit version (the usual outcome of an unhandled event is a fault that goes through the retry policy and ends in the `_error` queue).
- **Note:** [checkout-saga.md §9](../checkout-saga.md) says such messages are "dropped by a state-machine guard". That statement is not backed by configuration in the code — treat the code as the truth.
- **Production fix:** declare explicit behaviour for every `(state, event)` pair you can legally receive late, and add `OnMissingInstance` handling.

### 2.2 A late `PaymentSucceeded` after a payment timeout has no refund path

- **Scenario:** Payment.API is slow. The 30 s payment timeout fires, the saga moves to `CompensatingStock` and asks Inventory to release the stock. Then Payment.API finally debits the account and publishes `PaymentSucceeded`.
- **Result:** the money is taken, the stock is released and the order is cancelled. Nothing in the code refunds the customer.
- **Production fix:** treat a late success as an event that triggers a **refund** compensation, or make the payment step idempotent with a "cancel payment" command that the saga sends on timeout.

### 2.3 A reservation can be created for an already-cancelled order

If RabbitMQ is down long enough for the 30 s *stock* timeout to fire, the saga cancels the order. When the broker recovers, the (outboxed) reserve request is still delivered and Inventory reserves stock for an order that is already cancelled. Because that reservation never reaches the payment step, nothing releases it. [checkout-saga.md §11.3](../checkout-saga.md) calls this the one uncompensated edge.

### 2.4 Timeouts are single-replica only

- **Where:** [Program.cs](../../src/SimpleStore.Checkout.API/Program.cs) (Quartz persistent store without clustering)
- **Why:** running two Checkout.API replicas against the same Quartz tables without `UseClustering()` can make both fire the same trigger.
- **Fix:** enable Quartz clustering (`UseClustering()` and a `SchedulerId` of `AUTO`), as noted in [v8b](../v8b-durable-store-for-saga-timeouts.md).

---

## 3. Order service

### 3.1 The server trusts the client's prices

- **Where:** `OrderService.CreateOrderAsync` in [OrderService.cs](../../src/SimpleStore.Order.API/Services/OrderService.cs)
- **What happens:** `ProductName` and `UnitPrice` come straight from the request. There is no call to Catalog, so a caller who talks to the API directly can send any price.
- **Why acceptable:** it keeps Order independent of Catalog at runtime (no synchronous coupling) and keeps the example short.
- **Production fix:** re-price server-side — either call Catalog, or keep a local price snapshot fed by `ProductUpdatedEventV1`.

### 3.2 Order status transitions are not enforced

- **Where:** [OrderStatus.cs](../../src/SimpleStore.Order.API/Models/OrderStatus.cs), `UpdateStatusAsync`
- **What happens:** the consumers and the admin `PATCH` overwrite `Status` unconditionally. An admin can move `Cancelled` back to `Pending`, and a late `OrderConfirmed` would overwrite a `Cancelled` status.
- **Production fix:** put the transition rules inside the order entity (`order.Confirm()`, `order.Cancel()`) and reject illegal moves.

---

## 4. Authentication and sessions

### 4.1 Refresh-token reuse is rejected but not treated as an attack

- **Where:** [RefreshTokenService.cs](../../src/SimpleStore.Identity.API/Services/RefreshTokenService.cs)
- **What happens:** tokens rotate on every use and the old one is revoked. Presenting an old token simply fails (401). The `ReplacedByTokenHash` column is written but never used to revoke the rest of the token family.
- **Production fix:** on reuse of a revoked token, revoke every descendant token for that user (refresh-token reuse detection).

### 4.2 Web and Admin store sessions in process memory

- **Where:** `AddDistributedMemoryCache()` in the `Program.cs` of Web and Admin
- **Effect:** sessions (`ss_session`) are lost when the app restarts and cannot be shared by two instances.
- **Fix:** register a Redis-backed `IDistributedCache` (the project already runs Redis for the cart).

### 4.3 One refresh path is not coalesced

- **Where:** the JwtBearer `OnMessageReceived` event in Web and Admin
- **What happens:** the single-flight `TokenRefreshCoordinator` protects the *outbound* `BearerTokenHandler`, but the inbound `OnMessageReceived` refresh calls Identity directly. Two simultaneous page loads with an expiring token could race.

### 4.4 Demo credentials and development secrets

The seeded users `admin@simplestore.local` and `demo@simplestore.local` use well-known passwords and are created at startup. This is for the demo only; never seed fixed credentials in a real deployment, and always keep `jwt-key` in a secret store.

---

## 5. Event sourcing / projections

### 5.1 The projector is a single instance

The projector subscribes to `$all` with one consumer. Running two Inventory replicas would apply every event twice (the per-event idempotency guards would make it *correct*, but wasteful). Fix: KurrentDB persistent subscriptions with a consumer group, or leader election.

### 5.2 A cancel for an unknown reservation is skipped, never retried

`ApplyStockReservationCancelledAsync` does nothing when the `reservations` read row is missing or not `Active`. Because `$all` is strictly ordered and the saga only cancels after it has received `StockReservedEventV1`, this guard only matters if the read model was partially wiped or edited. It is a defensive check worth understanding, not a live race.

### 5.3 Reconnect back-off is not reset after progress

In `InventoryProjectionService`, the 1 s → 30 s back-off is reset only when the subscription loop returns cleanly. After a long healthy period, the next failure keeps the previous (possibly 30 s) delay.

### 5.4 Reservation "commit" is not implemented

Reservations are created and can be cancelled (v12), but there is no step that converts a confirmed reservation into a delivery note. Stock is decremented at reservation time and simply stays that way for confirmed orders.

### 5.5 The projector-lag gauge measures bytes, not events

`simplestore.inventory.projector.lag` is the difference between commit positions (a byte offset in the transaction log), and the "tail" it uses is the last position the subscription has *seen*, not the real tail of the log.

---

## 6. Infrastructure and operations

| Topic | Today | Production |
|---|---|---|
| Database migrations | applied automatically at service start | run as a separate deployment step |
| Message broker | one RabbitMQ container, no clustering | clustered/quorum queues, dead-letter handling and alerts on `_error` queues |
| Secrets | AppHost user-secrets | a secret manager (Key Vault, Vault, ...) |
| TLS | development certificates | real certificates, mTLS between services |
| Gateway | no rate limiting, no request size limits | rate limiting, WAF, request limits |
| Tests | the repository has **no test projects** | unit tests for aggregates and the saga (`MassTransit.Testing` harness), integration tests with Testcontainers |
| Data retention | outbox/inbox tables grow | enable MassTransit outbox cleanup, archive old events |

---

## 7. Documentation drift you may notice

While reading the older documents you may find statements that no longer match the code. The current sources of truth are the code and this guide.

- [checkout-saga.md](../checkout-saga.md) sections 2–12 describe the **v8** flow (no payment step). Section 15 is the current v12 flow.
- The same document mentions a `RowVersion` column on the saga state, an `UnknownProduct` failure reason and a table named `CheckoutSagaState`. The real table is `checkout_saga_state`, concurrency is `Pessimistic` locking, and Inventory only emits `InsufficientStock`.
- Some endpoint `Results.Created(...)` URLs omit the `/v1` segment.
- A few code comments (for example about inbox usage in `CatalogDbContext` and `InventoryReadDbContext`) predate later versions.

## 8. Possible defects found while writing this guide

These are different from the deliberate simplifications above: they look like **bugs or surprises**, found while reading and testing the code for this guide. Each one says how well it was verified.

### 8.1 `[MessageUrn("urn:message:...")]` is rejected by MassTransit 9.2.1 — *reproduced*

- **Where:** every event in [`src/SimpleStore.Contracts`](../../src/SimpleStore.Contracts/) (for example `OrderSubmittedEventV1`).
- **What happens:** the attributes are written with the full `urn:message:` prefix. In a scratch console project referencing `SimpleStore.Contracts` and `MassTransit.Abstractions` 9.2.1, `MessageUrn.ForType(typeof(OrderSubmittedEventV1))` threw `ArgumentException: Value should not contain the default prefix 'urn:message:'` (wrapped in a `TypeInitializationException`).
- **Impact:** MassTransit resolves the URN when it publishes or consumes a message, so the same exception is the expected result in the running services. *Not verified end to end* — the AppHost was not run for this check. If your build runs fine, the pinned package version or your local state differs; please check.
- **Likely fix:** drop the prefix in each attribute, e.g. `[MessageUrn("SimpleStore.Contracts:OrderSubmittedEvent")]`. The scratch test produced the same final URN with that form.

### 8.2 The URN pin does not keep exchange names stable — *observed in a scratch test*

The v11 notes say the `V1` rename is invisible on the bus. That is true for the URN inside the message envelope, but in the scratch test the exchange name was derived from the CLR type name, not from the URN. After the rename, exchanges are therefore named `SimpleStore.Contracts:<Name>V1`. This is harmless while all services are rebuilt and redeployed together, but not a guarantee for rolling upgrades.

### 8.3 MassTransit 9 licensing — *to verify*

An in-memory bus test on 9.2.1 failed with a message asking for a license (`SetLicense` / `MT_LICENSE`). No license configuration was found in the repository. Check the licensing terms of the MassTransit version you deploy.

### 8.4 Events backlogged while Inventory was down are not published — *from reading the code*

`KurrentEventStore.SubscribeAllAsync` sets `IsLive` only after KurrentDB's `CaughtUp` marker, for **every** subscription, including a normal restart that resumes from a checkpoint. Events appended while Inventory was down are replayed with `IsLive = false`: the read tables update, but `StockReservedEventV1` and the other integration events are not published. The saga would only recover through its 30 s timeout. See [chapter 7](07-inventory-event-sourcing-cqrs.md).

### 8.5 One permanently failing event stalls the projector — *from reading the code*

On an exception the outer loop reloads the same checkpoint and retries the same event every 30 s at most, so nothing after it is projected until it is fixed. A dead-letter or "skip and alert" policy is the usual remedy.

### 8.6 Smaller surprises in the web apps

- The `Remember me` checkbox on the Web login form is bound but never read.
- Login keeps an existing `ss_session` id instead of issuing a new one (a session-fixation consideration).
- A failed cart merge is swallowed and the `ss_cart` cookie is cleared anyway, orphaning the anonymous cart until it expires.
- In the Web order list, `Cancelled` orders get the same green badge as `Confirmed` ones; read the status text.
- `ProductUpdatedEventV1` has no version or timestamp, so two quickly repeated edits delivered out of order could leave a cart with the older values.

## Key takeaways

- A teaching project optimises for **clarity**, not for completeness; knowing where it cuts corners is part of the lesson.
- Most items above are one of three families: *missing concurrency control*, *missing handling of unexpected message order*, or *single-instance assumptions*.
- Good exercises: fix 1.2 (payment locking), 2.2 (refund on late success), 3.2 (state transitions in the entity), 4.2 (Redis session store).

**Back to the** [guide index](README.md).
