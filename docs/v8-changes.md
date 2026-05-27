# v8 Changes — Add Checkout Saga for Order/Stock Orchestration

## Overview

Version 8 is the release where **SimpleStore moves from simple event-driven integration to a real distributed workflow**.

Before v8, order creation and stock updates were loosely connected, and Catalog still reacted directly to order submission to change stock. In v8, the system introduces a dedicated **checkout orchestrator** (`SimpleStore.Checkout.API`) that coordinates three separate services:

- **Order.API** creates the order
- **Checkout.API** runs the saga
- **Inventory.API** decides whether stock can be reserved

The result is a more realistic microservices design:

- orders are created as **`Pending`** first
- stock is checked and reserved **asynchronously**
- the order is later moved to **`Confirmed`** or **`Cancelled`**
- **Inventory** becomes the single source of truth for stock
- **Catalog** stops owning stock logic and becomes a cache of stock values

This is a major architectural step because the system now models a classic microservices problem: **how do you coordinate business work across multiple services and databases without using one shared transaction?**

---

## Why This Matters

### 1. Distributed transactions are hard in microservices

In a monolith, placing an order and decrementing stock could happen in one database transaction.

In v8, that is no longer possible because the workflow spans:

- `orderdb`
- `checkoutdb`
- `inventorydb`
- KurrentDB (Inventory write side)
- RabbitMQ

That means the system cannot rely on one ACID transaction across the whole checkout flow.

So v8 applies the **Saga pattern**.

### 2. The Saga pattern gives coordination without 2PC

A saga breaks a business process into **local transactions** owned by different services. Each step succeeds independently, and the workflow moves forward using messages.

That is exactly what happens here:

1. Order.API saves the order locally
2. Checkout.API asks Inventory to reserve stock
3. Inventory either succeeds or fails locally
4. Checkout.API publishes the final outcome
5. Order.API updates order status locally

Instead of one giant commit, the system uses **events, persisted state, and compensation**.

### 3. Orchestration vs choreography

v8 is a strong example of **orchestration**.

- In **choreography**, each service reacts to events without a central coordinator
- In **orchestration**, one component explicitly drives the workflow

SimpleStore chooses orchestration by introducing `SimpleStore.Checkout.API`.

That matters educationally because it makes the flow easier to understand:

- one service owns the workflow state
- one service decides what happens next
- one service handles timeout and failure paths

This is often easier for learners than a purely choreographed event mesh.

### 4. Stock ownership becomes clearer

A second important microservices principle appears in v8: **single source of truth**.

Before this version, Catalog was still directly changing stock. After v8:

- **Inventory owns stock truth**
- **Catalog only caches stock for read/display purposes**

That is a healthier service boundary. Catalog is about product information. Inventory is about stock movement and reservation.

---

## What Changed

### 1. A new `Checkout.API` microservice was added

The biggest change in the diff is the new project:

- `src/SimpleStore.Checkout.API`

This service has **no HTTP API** and **no JWT auth setup** because it is not called by browsers or UI apps. It is a pure background orchestrator.

From the diff:

```csharp
// Pure consumer/orchestrator: NO HTTP surface, NO JWT (it only reacts to RabbitMQ messages).
// It consumes OrderSubmittedEvent, drives a MassTransit saga state machine, asks Inventory.API to
// reserve stock, and tells Order.API whether to confirm or cancel the order.
```

That comment captures the design intent well. This service does not own orders or stock. It owns the **workflow**.

#### Why create a separate service?

Because it keeps responsibilities clean:

- **Order.API** owns order persistence
- **Inventory.API** owns stock reservation
- **Checkout.API** owns long-running coordination

That separation is a classic microservices lesson: if a workflow spans multiple services, the workflow itself can become a first-class service.

---

### 2. The saga is implemented with a MassTransit state machine

The saga is modeled explicitly in `CheckoutSagaStateMachine.cs`.

The heart of the change looks like this:

```csharp
//   Initial --OrderSubmitted--> AwaitingStock      (publish ReserveStockRequested, schedule timeout)
//   AwaitingStock --StockReserved--> Confirmed     (publish OrderConfirmed)
//   AwaitingStock --StockReservationFailed--> Cancelled  (publish OrderCancelled, reason from msg)
//   AwaitingStock --timeout--> Cancelled           (publish OrderCancelled, reason "ReservationTimeout")
```

This is important because the checkout workflow is now represented as a **real state machine**, not just scattered `if` statements across services.

The service wiring is also educational because it shows the infrastructure a saga needs:

```csharp
x.AddSagaStateMachine<CheckoutSagaStateMachine, CheckoutSagaState>()
    .EntityFrameworkRepository(r =>
    {
        r.ConcurrencyMode = ConcurrencyMode.Pessimistic;
        r.ExistingDbContext<CheckoutDbContext>();
        r.UsePostgres();
    });

x.AddQuartzConsumers();
x.AddPublishMessageScheduler();
```

That small block teaches three useful ideas:

- the saga instance is **persisted in Postgres**
- concurrent messages are controlled with **pessimistic locking**
- time-based workflow behavior is implemented with a **scheduler**, not a sleeping thread

#### Persisted saga state

The saga stores durable workflow data in `CheckoutSagaState`:

```csharp
public class CheckoutSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public int OrderId { get; set; }
    public string? UserId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid? TimeoutTokenId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

Why persist this?

Because a saga is not just “some events flying around.” It is a **business process with memory**. The system needs to remember:

- which order is being processed
- what reservation was requested
- which state the workflow is in
- why it failed
- whether a timeout is pending

That is why AppHost also adds a new database:

```csharp
var checkoutDb = postgres.AddDatabase("checkoutdb");
```

The lesson: **workflow state needs a home**.

---

### 3. Saga states and transitions

The v8 workflow is intentionally small and readable.

#### State: `Initial`

The saga starts when it receives `OrderSubmittedEvent`.

At that moment it:

- correlates the workflow using `CorrelationId`
- stores order metadata
- generates a `ReservationId`
- schedules a timeout
- publishes `ReserveStockRequestedEvent`
- transitions to `AwaitingStock`

From the diff:

```csharp
Initially(
    When(OrderSubmitted)
        .Then(ctx =>
        {
            ctx.Saga.OrderId = ctx.Message.OrderId;
            ctx.Saga.UserId = ctx.Message.UserId;
            ctx.Saga.ReservationId = Guid.NewGuid();
        })
        .Schedule(ReservationTimeout, ctx => new ReservationTimeoutExpired { CorrelationId = ctx.Saga.CorrelationId })
        .Publish(ctx => new ReserveStockRequestedEvent
        {
            CorrelationId = ctx.Saga.CorrelationId,
            ReservationId = ctx.Saga.ReservationId,
            OrderId = ctx.Saga.OrderId,
            ...
        })
        .TransitionTo(AwaitingStock));
```

#### State: `AwaitingStock`

This is the waiting state. The order exists, but checkout is not complete yet.

This is a subtle but important microservices idea: **the business process is in progress even though the initial API call already returned**.

That is why `Order.API` now creates orders with:

```csharp
Status = "Pending"
```

The order is real, but its final business outcome is not known yet.

#### State: `Confirmed`

If Inventory publishes `StockReservedEvent`, the saga:

- cancels the timeout
- publishes `OrderConfirmedEvent`
- transitions to `Confirmed`
- finalizes the saga

#### State: `Cancelled`

If Inventory publishes `StockReservationFailedEvent`, or if the timeout fires first, the saga:

- records the failure reason
- publishes `OrderCancelledEvent`
- transitions to `Cancelled`
- finalizes the saga

This gives learners a clean model:

- **happy path** → confirm
- **known business failure** → cancel
- **operational failure/slow response** → cancel

---

### 4. Order placement now coordinates with inventory instead of changing stock directly

This is one of the most important business changes in the diff.

#### Before v8

Order creation emitted `OrderSubmittedEvent`, and Catalog reacted by decrementing stock.

That meant Catalog was doing work that really belonged to Inventory.

#### After v8

Order creation still publishes `OrderSubmittedEvent`, but now that event starts the saga.

`OrderService` now generates a correlation id and includes it in the event:

```csharp
var order = new OrderEntity
{
    CorrelationId = Guid.NewGuid(),
    UserId = userId,
    ...
    Status = "Pending"
};

await _publishEndpoint.Publish(new OrderSubmittedEvent
{
    CorrelationId = order.CorrelationId,
    OrderId = order.Id,
    ...
}, ct);
```

Why add `CorrelationId`?

Because distributed workflows need a stable identifier that every participant can use. This is the thread that ties the whole saga together.

The flow is now:

1. Order is created in Order.API
2. Saga receives `OrderSubmittedEvent`
3. Saga requests stock reservation from Inventory
4. Inventory decides success or failure
5. Saga publishes final business outcome
6. Order.API updates the order row

That means **order placement is no longer “create order and assume stock is fine.”** It becomes a cross-service workflow with explicit coordination.

---

### 5. Inventory gained a reservation step

Inventory is no longer just projecting stock movements for notes. It now participates directly in checkout.

The new consumer bridges the integration event into application logic:

```csharp
public sealed class ReserveStockRequestedConsumer : IConsumer<ReserveStockRequestedEvent>
{
    public async Task Consume(ConsumeContext<ReserveStockRequestedEvent> context)
    {
        var msg = context.Message;
        var cmd = new CreateReservationCommand(
            msg.CorrelationId,
            msg.ReservationId,
            msg.OrderId,
            msg.Lines.Select(l => new ReservationCommandLine(l.ProductId, l.Quantity)).ToList());

        await _handler.HandleAsync(cmd, context.CancellationToken);
    }
}
```

This is a good example of a clean message boundary:

- the bus carries a simple contract
- the consumer translates that contract into an internal command
- the application handler executes the domain logic

#### How stock is checked

`CreateReservationHandler` does a `SELECT ... FOR UPDATE` on projected stock rows:

```csharp
var levels = await _readDb.StockLevels
    .FromSqlInterpolated($"SELECT * FROM stock_levels WHERE \"ProductId\" = ANY({ids}) FOR UPDATE")
    .ToDictionaryAsync(s => s.ProductId, ct);
```

Why this matters:

- it serializes concurrent reservation checks for the same products
- it shows that even event-driven systems still need careful concurrency control
- it demonstrates that sagas do **not** eliminate consistency concerns; they relocate them into local-service logic

#### Success path in Inventory

If stock is available, Inventory appends a domain event to KurrentDB:

```csharp
await _eventStore.AppendAsync(
    $"reservation-{reservation.Id}", reservation.UncommittedEvents, AppendCondition.NoStream, ct);
```

This is important architecturally:

- the write-side truth remains in the event store
- reservation is modeled as a domain event, not just a row update
- idempotency is improved because the stream name uses `ReservationId`

#### Failure path in Inventory

If stock is insufficient, Inventory does **not** append a domain event. Instead, it publishes a failure integration event:

```csharp
await _publishEndpoint.Publish(new StockReservationFailedEvent
{
    CorrelationId = cmd.CorrelationId,
    ReservationId = cmd.ReservationId,
    OrderId = cmd.OrderId,
    Reason = "InsufficientStock",
    ShortageLines = shortages,
    FailedAt = _clock.GetUtcNow()
}, ct);
```

That is educationally useful because it teaches an important distinction:

- **domain events** describe something that happened inside a domain
- **integration events** can also describe a rejected outcome to another service

In other words, “no reservation was created” is still important information for the saga.

---

### 6. The projector now publishes integration events too

Inventory’s projector used to only build read models. In v8 it also publishes integration events when live events are applied.

That change is visible here:

```csharp
if (isLive)
{
    await _publish.Publish(new StockReservedEvent
    {
        CorrelationId = evt.CorrelationId,
        ReservationId = evt.NoteId,
        OrderId = evt.OrderId,
        ReservedAt = evt.ReservedAt,
        ...
    }, ct);
}
```

And it also publishes `StockLevelChangedEvent` whenever on-hand stock changes.

Why publish from the projector instead of from the command handler?

Because Inventory’s write side is event-sourced. The system wants the outward message to happen only when:

1. the domain event exists
2. the projection/read model update succeeds
3. the checkpoint advances

That gives better consistency between:

- the read model
- the integration events
- downstream consumers

The projector comment explains the intent clearly:

```csharp
// the read-model write, the checkpoint, and the outbound events all commit atomically.
```

That is a sophisticated microservices lesson: **even in an eventually consistent system, you can still make certain boundaries atomic locally**.

> Note: v8 also guards this with `isLive`, so a replay does not re-publish historical business events.

---

### 7. Compensation logic was added for failure cases

Sagas are not just about happy-path coordination. They are about **what to do when one step fails after another already succeeded**.

In SimpleStore v8, compensation is intentionally simple:

- the order is already created as `Pending`
- if stock reservation fails, the order is moved to `Cancelled`
- if inventory never responds in time, the order is also moved to `Cancelled`

The state machine makes that explicit:

```csharp
When(StockReservationFailed)
    .Publish(ctx => new OrderCancelledEvent
    {
        CorrelationId = ctx.Saga.CorrelationId,
        OrderId = ctx.Saga.OrderId,
        Reason = ctx.Message.Reason,
        CancelledAt = DateTimeOffset.UtcNow
    })
    .TransitionTo(Cancelled)
    .Finalize()
```

and:

```csharp
When(ReservationTimeout.Received)
    .Publish(ctx => new OrderCancelledEvent
    {
        CorrelationId = ctx.Saga.CorrelationId,
        OrderId = ctx.Saga.OrderId,
        Reason = "ReservationTimeout",
        CancelledAt = DateTimeOffset.UtcNow
    })
```

#### Why is cancellation the compensation?

Because the only thing already committed before reservation is the order row itself.

So the compensating action is not “undo a distributed transaction.” It is:

- keep the historical record that the customer attempted checkout
- mark the business outcome as cancelled
- avoid confirming an order that cannot be fulfilled

This is exactly how real sagas work: **they replace rollback with explicit compensating actions**.

---

### 8. Order.API now reacts to final outcome events

Order no longer decides its final status at creation time.

Instead, it listens for the saga outcome:

- `OrderConfirmedEvent`
- `OrderCancelledEvent`

Example consumer:

```csharp
var order = await _context.Orders.FirstOrDefaultAsync(
    o => o.CorrelationId == msg.CorrelationId, context.CancellationToken);

order.Status = "Confirmed";
await _context.SaveChangesAsync(context.CancellationToken);
```

and similarly for cancellation.

This is educationally important because it shows a common microservices technique:

- create a business entity in an intermediate state
- wait for asynchronous cross-service work
- update the entity later when the workflow finishes

That is much more realistic than pretending every multi-service operation can finish inside one request/response cycle.

---

### 9. Catalog stopped being the stock owner

A subtle but very important v8 change is that Catalog no longer mutates stock based on order submission.

Instead, Catalog listens for `StockLevelChangedEvent` from Inventory:

```csharp
product.Stock = msg.NewOnHand;
await _context.SaveChangesAsync(ct);
```

The surrounding code explains the design:

```csharp
// Inventory's projector publishes StockLevelChangedEvent whenever stock_levels changes
// ... we overwrite Product.Stock with the authoritative NewOnHand.
```

This teaches two architecture lessons:

1. **ownership must be explicit**
2. **read models can be denormalized caches owned by another service**

Catalog still exposes stock to storefront users, but it is no longer the source of truth.

That is a cleaner domain split.

---

### 10. Aspire orchestration was updated to reflect the new topology

`SimpleStore.AppHost` now wires the new service graph.

Key additions from the diff:

```csharp
var checkoutDb = postgres.AddDatabase("checkoutdb");
```

```csharp
var checkout = builder.AddProject<Projects.SimpleStore_Checkout_API>("checkout")
    .WithReference(checkoutDb)
    .WithReference(rabbitmq)
    .WaitFor(checkoutDb)
    .WaitFor(rabbitmq);
```

Inventory also now references RabbitMQ:

```csharp
var inventory = builder.AddProject<Projects.SimpleStore_Inventory_API>("inventory")
    .WithReference(inventoryDb)
    .WithReference(kurrentdb)
    .WithReference(rabbitmq)
```

Why this matters:

The architecture is no longer just a set of isolated services. The runtime topology now explicitly models:

- a new persistent database for saga state
- a message bus dependency for Inventory
- a dedicated background orchestrator service

This is a good reminder that microservices architecture is not only code-level design. It is also **deployment wiring and runtime composition**.

---

## Architecture Diagram

```text
Customer/Web
    |
    | POST /api/order/orders
    v
Order.API
    | 1. Save Order(Status=Pending, CorrelationId)
    | 2. Publish OrderSubmittedEvent
    v
RabbitMQ
    v
Checkout.API (Saga Orchestrator)
    | 3. Create saga state in checkoutdb
    | 4. Schedule 30s timeout
    | 5. Publish ReserveStockRequestedEvent
    v
RabbitMQ
    v
Inventory.API
    | 6a. If stock available:
    |       append StockReservedV1 to KurrentDB
    |       projector updates inventorydb
    |       projector publishes StockReservedEvent
    |
    | 6b. If stock unavailable:
    |       publish StockReservationFailedEvent
    v
RabbitMQ
    v
Checkout.API (Saga)
    | 7a. On StockReservedEvent -> publish OrderConfirmedEvent
    | 7b. On StockReservationFailedEvent -> publish OrderCancelledEvent
    | 7c. On timeout -> publish OrderCancelledEvent
    v
RabbitMQ
    v
Order.API
    | 8a. Confirmed -> Status = Confirmed
    | 8b. Cancelled -> Status = Cancelled
    v
orderdb

Side channel:
Inventory projector -> StockLevelChangedEvent -> Catalog.API updates cached Product.Stock
```

---

## Key Takeaways

### 1. A saga is a workflow with persisted state

It is not just “event-driven code.” The saga stores durable state so the system can track progress across messages and time.

### 2. Local transactions replace one global transaction

Each service commits its own data independently:

- Order commits order creation
- Checkout commits saga state changes
- Inventory commits reservation/event-store work
- Order later commits final status change

That is the essence of saga-based consistency.

### 3. Compensation replaces rollback

When later steps fail, the system does not roll back earlier commits across all services. It performs a compensating business action instead.

In v8, that compensation is cancelling the order.

### 4. Orchestration makes the workflow explicit

Having a dedicated orchestrator service is often easier to reason about than a purely choreographed mesh, especially for long-running or failure-sensitive workflows.

### 5. Single source of truth matters

Inventory now owns stock truth. Catalog only mirrors stock for display.

That is a better microservices boundary than having multiple services mutate stock independently.

### 6. Event sourcing and sagas can work together

Inventory is event-sourced, while Checkout is a saga orchestrator. v8 shows that these patterns are complementary, not competing.

---

## Trade-offs

### Saga vs 2PC

#### Advantages of saga-based coordination

- works across heterogeneous stores and services
- avoids tight coupling to a distributed transaction coordinator
- scales better operationally in microservice environments
- matches asynchronous, message-driven architectures well

#### Costs of saga-based coordination

- business consistency becomes eventual, not immediate
- failure handling is more complex
- you need explicit compensation logic
- debugging often requires tracing messages across services

In other words, sagas are usually more practical than 2PC in microservices, but they are also more visible to developers.

### Orchestration vs choreography

#### Why orchestration is good here

- one place owns the workflow definition
- timeout handling has a clear home
- business transitions are easy to visualize
- learners can understand the checkout flow by reading one state machine

#### What orchestration costs

- introduces a central coordinator service
- creates one more deployable unit and database
- can become a bottleneck if every workflow is forced through one orchestrator

### Additional v8 trade-offs visible in the diff

#### 1. Timeout scheduling uses in-memory Quartz

This keeps the sample simple, but scheduled timeouts do not survive a Checkout.API restart.

That is acceptable for a learning project, but it is a real production trade-off.

#### 2. Inventory checks projected stock

`SELECT ... FOR UPDATE` helps, but the comment in the handler documents a race window because stock is decremented asynchronously by the projector.

That means v8 favors architectural clarity over perfect reservation semantics.

#### 3. More moving parts

Compared with earlier versions, v8 adds:

- a new service
- a new database
- more contracts
- more message flows
- more operational concepts to understand

That complexity is the price of modeling distributed business workflows honestly.

---

## Final Summary

v8 is the version where SimpleStore becomes a much stronger teaching example of microservices architecture.

It introduces:

- a **MassTransit saga state machine**
- a dedicated **Checkout orchestrator service**
- explicit **Pending -> Confirmed/Cancelled** order flow
- **Inventory-owned stock reservation**
- **compensation via cancellation**
- a clearer distinction between **service ownership**, **integration events**, and **eventual consistency**

For learners, the big lesson is this:

> When a business process spans multiple services, the solution is usually not a bigger transaction. It is a better workflow model.

That is exactly what v8 delivers.
