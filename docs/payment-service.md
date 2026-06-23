# Payment Service — Design Specification

> Introduced in **v12**. `SimpleStore.Payment.API` is a deliberately simple prepaid-balance payment service whose purpose is to make the checkout saga's outcome **controllable** for demos, and — by sitting after stock reservation — to give the saga a real **compensation** to perform when it fails. See [checkout-saga.md](checkout-saga.md) for the saga itself and [v12-changes.md](v12-changes.md) for the changelog.

---

## 1. Purpose

A "payment" in a real shop can fail for many reasons. For a demo we want **one deterministic, operator-controlled reason**: the account doesn't have enough money. So Payment.API models a prepaid wallet:

- Each customer has one **account** with a **balance** (auto-provisioned at zero on first access).
- Customers **deposit** funds (top-up). Admins can deposit on a customer's behalf.
- When the checkout saga asks Payment.API to charge an order, it **succeeds iff the balance covers the amount**, debiting it; otherwise it **fails**.

This makes a demo trivial to drive: set the balance high → checkout confirms; leave it low → checkout cancels **and the reserved stock is released** (the compensation).

It is intentionally *not* a real payment system — no authorize/capture, no external PSP, no idempotency keys beyond the bus inbox, no currency handling. Single debit on request.

---

## 2. Data model (`paymentdb`)

```
payment_accounts
  Id           uuid     PK
  UserId       text     UNIQUE     -- soft ref to AspNetUsers.Id (identitydb); no cross-DB FK
  Balance      numeric(18,2)
  CreatedAt    timestamptz
  UpdatedAt    timestamptz

payment_transactions                -- append-only ledger
  Id            uuid    PK
  AccountId     uuid    FK -> payment_accounts
  Type          text               -- "Deposit" | "Payment" (enum HasConversion<string>)
  Amount        numeric(18,2)
  BalanceAfter  numeric(18,2)       -- running balance snapshot, so history renders without recompute
  OrderId       int     null        -- set on Payment rows only
  CorrelationId uuid    null        -- the saga correlation, on Payment rows only
  Description   text    null
  CreatedAt     timestamptz
```

Plus the MassTransit transactional inbox/outbox tables (`AddInboxStateEntity` / `AddOutboxMessageEntity` / `AddOutboxStateEntity`).

`OnHand`-style negative balances are not allowed — a debit that would overdraw is rejected (that *is* the failure).

---

## 3. Role in the checkout saga

```mermaid
sequenceDiagram
    autonumber
    participant Saga as Checkout.API (saga)
    participant Bus as RabbitMQ
    participant Pay as Payment.API
    participant DB as paymentdb

    Note over Saga: state AwaitingPayment (stock already reserved)
    Saga->>Bus: ProcessPaymentRequestedEventV1 { CorrelationId, OrderId, UserId, Amount }
    Bus->>Pay: ProcessPaymentRequestedEventV1
    activate Pay
    Pay->>DB: BEGIN tx
    Pay->>DB: SELECT/UPSERT account by UserId
    alt Balance >= Amount
        Pay->>DB: Balance -= Amount; INSERT Payment txn
        Pay->>DB: INSERT OutboxMessage(PaymentSucceededEventV1)
    else insufficient
        Pay->>DB: INSERT OutboxMessage(PaymentFailedEventV1, Reason=InsufficientFunds)
    end
    Pay->>DB: COMMIT
    deactivate Pay
    Pay->>Bus: PaymentSucceeded / PaymentFailed
    Bus->>Saga: result
    Note over Saga: Succeeded → Confirmed; Failed → CompensatingStock (release stock) → Cancelled
```

The debit + reply commit in one Postgres transaction (EF bus outbox), exactly like `OrderService.CreateOrderAsync`. The **EF inbox** dedupes redelivery, so the saga's at-least-once delivery never double-charges.

---

## 4. Events (in `SimpleStore.Contracts`)

| Event | Fields | Direction |
|---|---|---|
| `ProcessPaymentRequestedEventV1` | CorrelationId, OrderId, UserId, Amount, RequestedAt | saga → Payment |
| `PaymentSucceededEventV1` | CorrelationId, OrderId, TransactionId, Amount, PaidAt | Payment → saga |
| `PaymentFailedEventV1` | CorrelationId, OrderId, Reason, Amount, FailedAt | Payment → saga |

`Reason` uses the `PaymentFailureReason` constants (today only `InsufficientFunds`). All three follow the v11 contract convention (`Vn` CLR type, `int Version`, pinned `MessageUrn`); see [versioning.md](versioning.md).

---

## 5. HTTP surface (`/api/v1/payment`, JWT)

**User** (`RequireAuthorization()`, owner resolved from the `sub` claim):

| Method | Route | Purpose |
|---|---|---|
| GET | `/account` | The caller's account (auto-provisions at zero). |
| POST | `/account/deposit` | Top up; body `DepositRequest { Amount }`. |
| GET | `/account/transactions` | The caller's ledger. |

**Admin** (`RequireAuthorization("Admin")`):

| Method | Route | Purpose |
|---|---|---|
| GET | `/admin/accounts` | Paged accounts. |
| GET | `/admin/accounts/count` | Total accounts. |
| GET | `/admin/accounts/{userId}` | One account (404 if none). |
| POST | `/admin/accounts/{userId}/deposit` | Deposit on a customer's behalf. |
| GET | `/admin/accounts/{userId}/transactions` | A customer's ledger. |

The gateway enforces the same split at the edge (`payment-user` = `AuthenticatedUser`, `payment-admin` = `Admin`); the service enforces it again (defense in depth).

---

## 6. UI

- **Web — Wallet** (`/Wallet`, `[Authorize]`): balance card with a deposit form (custom amount + quick +$50/+$100/+$500), and the transaction history. This is how a *customer* makes their own next checkout succeed.
- **Admin — Payments** (`/payments`, `[Authorize(Roles="Admin")]`): every customer with their balance (users joined to accounts in memory, the `Customers.razor` pattern), an inline deposit per row, and a per-customer ledger view. This is the *operator's* demo-setup surface.

---

## 7. Idempotency & concurrency

- **Redelivery:** the consume is wrapped by the MassTransit EF inbox on `PaymentDbContext`, so the same `ProcessPaymentRequestedEventV1` is processed once. A duplicate is dropped before `DebitForOrderAsync` runs.
- **Concurrency:** deposits and debits run inside an `IExecutionStrategy` transaction. A single account being mutated concurrently (e.g. a manual deposit racing a saga debit) is rare in the demo; lost-update hardening (xmin / `SELECT … FOR UPDATE`) is intentionally omitted for simplicity. If this graduated past a demo, add a concurrency token on `PaymentAccount`.

---

## 8. Known limitations (by design)

1. **No authorize/capture.** The charge is a single debit at saga time. A real flow would authorize on order placement and capture on fulfilment, with a void/refund compensation.
2. **No refund event.** Because payment is the *last* saga step, a failure there compensates *stock*, not payment — so there's no payment refund to model. (Reordering to pay-first would flip this.)
3. **No multi-currency, no rounding policy, no statements.**
4. **Accounts can't be pre-seeded** by the seeder (Identity's user GUIDs aren't known at seed time) — they auto-provision on first access instead.
