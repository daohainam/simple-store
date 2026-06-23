# v12 Changes — Payment service + saga compensation

## Overview

Through v11 the checkout saga was `OrderSubmitted → ReserveStock → Confirm/Cancel`. Its only failure path was "insufficient stock," and because a reservation immediately decremented `stock_levels.OnHand` and was never released, **a cancelled order left stock permanently debited** — there was no compensating action to demonstrate. The saga's defining feature (undo a completed step when a later step fails) couldn't be shown.

v12 adds a **`SimpleStore.Payment.API`** microservice and inserts a **payment step after stock reservation**. Payment succeeds or fails purely on the customer's account balance, so the balance becomes the **controllable gate** for a demo. And because payment runs *after* stock is already reserved, a payment failure triggers a real **compensation: release the reserved stock** (add the held quantity back to `OnHand`) before cancelling the order. That compensation is the reservation-cancel the [v8 checkout-saga doc](checkout-saga.md) marked "deferred to v9."

New saga flow:

```
OrderSubmitted ─▶ AwaitingStock ─StockReserved─▶ AwaitingPayment ─PaymentSucceeded─▶ Confirmed
                       │                               │
              StockReservationFailed          PaymentFailed / PaymentTimeout
              / ReservationTimeout                     │
                       ▼                               ▼
                   Cancelled                   CompensatingStock ─StockReservationCancelled─▶ Cancelled
                                               (publishes StockReservationCancelRequested)
```

Design detail lives in [payment-service.md](payment-service.md) and the updated [checkout-saga.md](checkout-saga.md). No breaking changes to existing events (all v12 events are brand new).

---

## 1. `SimpleStore.Payment.API` — new microservice (Postgres `paymentdb`)

**Files:** `src/SimpleStore.Payment.API/**`, `src/SimpleStore.Payment.API.Client/**`.

Mirrors the Order.API template (csproj packages, `Program.cs` shape, JWT block, MassTransit EF outbox+inbox, `AddSimpleStoreApiVersioning()`, `AddOpenApi("v1")`, `StartupMigrationRunner`).

- **Model.** `PaymentAccount` — one prepaid balance per user (`UserId` unique, soft reference to `identitydb`), auto-provisioned at zero on first access. `PaymentTransaction` — append-only ledger (`Deposit` / `Payment`, with `BalanceAfter` snapshot, optional `OrderId` / `CorrelationId`). `PaymentTransactionType` enum stored as a string.
- **Service.** `PaymentService` — `GetOrCreateAccountAsync`, `DepositAsync`, `GetTransactionsAsync`, admin `GetAccountsAsync` / `GetAccountByUserAsync`, and the saga-driven `DebitForOrderAsync`. Deposit and debit run inside `IExecutionStrategy.ExecuteAsync` + `BeginTransactionAsync` (the `OrderService.CreateOrderAsync` pattern).
- **Consumer.** `ProcessPaymentRequestedConsumer` → `DebitForOrderAsync`. If `Balance >= Amount`: debit, record a `Payment` row, publish `PaymentSucceededEventV1`. Else: publish `PaymentFailedEventV1` (`Reason = "InsufficientFunds"`). The publish rides the same transaction's EF bus outbox; the EF **inbox** makes the consume exactly-once, so a redelivered request never double-charges.
- **Endpoints** under `/api/v1/payment` — user (`RequireAuthorization()`, owner = `sub`): `GET /account`, `POST /account/deposit`, `GET /account/transactions`. Admin (`RequireAuthorization("Admin")`): `GET /admin/accounts` (paged), `/count`, `GET /admin/accounts/{userId}`, `POST /admin/accounts/{userId}/deposit`, `GET /admin/accounts/{userId}/transactions`.
- **Client.** `SimpleStore.Payment.API.Client` — `AccountDto`, `TransactionDto`, `DepositRequest`, `PagedResult<T>`, `IPaymentApiClient` / `PaymentApiClient`, `AddPaymentApiClient(builder, serviceName = "gateway")`.
- **Migration.** `InitialCreate` on `PaymentDbContext` (`payment_accounts`, `payment_transactions`, + MassTransit inbox/outbox tables).

> **Note:** the plan considered an `xmin` optimistic-concurrency token on `PaymentAccount`, but the installed Npgsql provider didn't expose `UseXminAsConcurrencyToken` cleanly; balance mutations run inside an `IExecutionStrategy` transaction instead, which is sufficient for the demo's single-account usage.

---

## 2. Checkout saga — payment step + compensation

**Files:** `src/SimpleStore.Checkout.API/Sagas/CheckoutSagaStateMachine.cs`, `Sagas/CheckoutSagaState.cs`, `Timeouts/PaymentTimeoutExpired.cs` (new), migration `AddPaymentToSaga`.

- **Two new states:** `AwaitingPayment` and `CompensatingStock`.
- **`CheckoutSagaState`** gained `decimal Amount` (order total, carried from `OrderSubmittedEventV1.TotalAmount`) and `Guid? PaymentTimeoutTokenId`.
- **`StockReserved`** no longer confirms directly — it unschedules the stock timeout, publishes `ProcessPaymentRequestedEventV1` (with `Amount` + `UserId`), schedules a 30 s `PaymentTimeoutExpired`, and moves to `AwaitingPayment`.
- **`PaymentSucceeded`** → unschedule payment timeout, publish `OrderConfirmedEventV1`, `Confirmed`, finalize.
- **`PaymentFailed`** (and the **payment timeout**) → store the reason, publish `StockReservationCancelRequestedEventV1` (the compensation), move to `CompensatingStock`.
- **`StockReservationCancelled`** (in `CompensatingStock`) → publish `OrderCancelledEventV1` (reason = stored `FailureReason`), `Cancelled`, finalize.
- The pre-existing `StockReservationFailed` / `ReservationTimeout` paths in `AwaitingStock` are unchanged — those failures need no compensation (nothing succeeded yet).
- Both timeouts use the existing Quartz persistent ADO store (v8b), so they survive a Checkout.API restart. Timeout is configurable via `Checkout:ReservationTimeoutSeconds` / `Checkout:PaymentTimeoutSeconds` (default 30 each).

No Order.API change was needed: `OrderCancelledConsumer` already maps any `Reason` string onto `OrderStatus.Cancelled`.

---

## 3. Inventory — reservation release (the compensation), finally implemented

**Files:** `src/SimpleStore.Inventory.API/Domain/Reservations/Reservation.cs`, `Domain/Reservations/Events/StockReservationCancelledV1.cs` (new), `EventStore/EventTypeRegistry.cs`, `Application/Reservations/CancelReservationCommand.cs` + `CancelReservationHandler.cs` (new), `Consumers/CancelReservationRequestedConsumer.cs` (new), `Projections/InventoryProjector.cs`, `Projections/InventoryProjectionService.cs`, `Program.cs`, `Observability/Telemetry.cs`.

- **Domain.** `Reservation.Cancel(now)` emits `StockReservationCancelledV1` (carrying the reserved lines), guarded so a double-cancel throws; `IsCancelled` exposes the state for the handler's idempotency check; `Apply` gained the cancel case. Wire type `simplestore.inventory.reservation.cancelled.v1`, registered in `EventTypeRegistry`.
- **Handler.** `CancelReservationHandler` rehydrates the aggregate from its KurrentDB stream, no-ops if already cancelled (`IsCancelled`), else appends the cancel event with `AppendCondition.StreamRevision(count-1)`; a `ConcurrencyConflictException` from a racing redelivery is treated as success. It touches no Postgres — no availability check is needed to release a hold.
- **Projector.** `ApplyStockReservationCancelledAsync` returns the held quantity to `stock_levels.OnHand` (positive delta, `MovementType = ReservationCancelled`), flips the read row `Status` to `"Cancelled"`, and — gated on `IsLive` — publishes `StockReservationCancelledEventV1` (to the saga) + `StockLevelChangedEventV1` (Catalog cache refresh). Idempotent: a reservation not `Active` is skipped.
- **No Inventory migration:** the read schema already had `ReservationRow.Status` and `StockMovementRow.MovementType`.

---

## 4. Contracts — 5 new events

**File:** `src/SimpleStore.Contracts/**`.

| Event | Direction |
|---|---|
| `ProcessPaymentRequestedEventV1` | saga → Payment |
| `PaymentSucceededEventV1` | Payment → saga |
| `PaymentFailedEventV1` (+ `PaymentFailureReason` constants) | Payment → saga |
| `StockReservationCancelRequestedEventV1` | saga → Inventory |
| `StockReservationCancelledEventV1` | Inventory → saga |

All follow the v11 convention: `Vn`-suffixed `sealed record`, `int Version` field, pinned `[MessageUrn("urn:message:SimpleStore.Contracts:<TypeNameWithoutV1>")]`. `StockReservationCancelledEventV1` reuses the existing `ReservationLineItem`. `StockChangeCause` gained a `ReservationCancelled` constant.

---

## 5. Host / gateway / UI

- **AppHost** (`AppHost.cs`, `.csproj`): `paymentdb` database, the `payment` project (references `paymentdb` + `rabbitmq` + `Jwt__*`), and `gateway.WithReference(payment)`.
- **Gateway** (`appsettings.json`): a `payment-cluster` + `payment-admin` (`Admin`) / `payment-user` (`AuthenticatedUser`) routes, mirroring the order routes.
- **Web**: a customer **Wallet** page (`WalletController` + `Views/Wallet/Index.cshtml`) — view balance, deposit (with quick +$50/+$100/+$500 buttons), and the transaction ledger; nav link added.
- **Admin**: a **Payments** page (`Components/Pages/Payments.razor`) — lists every customer with their balance (merging `IIdentityApiClient.GetUsersAsync` with `IPaymentApiClient.GetAccountsAsync`), deposits on a customer's behalf, and shows their ledger; nav link added.
- **Solution**: `SimpleStore.Payment.API` + `SimpleStore.Payment.API.Client` added to `SimpleStore.slnx`.

---

## 6. Demo

Run `dotnet run --project src/SimpleStore.AppHost`, then log into Web as `demo@simplestore.local` / `Demo123!`.

- **Compensation path:** with a **zero balance**, add an item and check out → the order ends **Cancelled** (reason `InsufficientFunds`); the Aspire dashboard shows the saga `AwaitingPayment → CompensatingStock → Cancelled`, and the product's stock returns to its pre-checkout value (released by the compensation — verify `stock_levels` in PgWeb and Catalog's `Product.Stock`).
- **Success path:** deposit enough on the Web **Wallet** page (or the Admin **Payments** page), check out again → **Confirmed**, balance debited, a `Payment` ledger row recorded, stock stays decremented.

Both outcomes are driven entirely by the account balance.
