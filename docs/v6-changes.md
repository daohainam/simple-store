# v6 Changes — Adding RabbitMQ and MassTransit for Event-Driven Flows

## Overview

Version 6 is the point where SimpleStore stops relying only on **synchronous request/response communication** between services and starts using an **event-driven architecture** for cross-service reactions.

This version introduces:

- **RabbitMQ** as the message broker
- **MassTransit** as the .NET messaging framework
- **`SimpleStore.Contracts`** as a shared home for integration events
- **publish/consume workflows** between `Order.API`, `Catalog.API`, and `Cart.API`
- **transactional outbox/inbox support** so events are reliable, not best-effort

In practical terms, v6 teaches an important microservices lesson:

> A service should not need to synchronously call another service every time something interesting happens. It can publish a fact, and other services can react in their own time.

That is exactly what SimpleStore starts doing here.

---

## Why This Matters

### 1. Event-driven architecture

Before v6, most service-to-service integration in SimpleStore was centered on **HTTP APIs**. That works well for request/response operations such as:

- loading products
- submitting login credentials
- reading a cart
- creating an order from the UI

But not every interaction should be a direct HTTP call.

When an order is placed, multiple parts of the system may care:

- Catalog may need to update stock
- Analytics may want order metrics later
- Notifications might email the customer in the future
- Inventory may eventually become the stock authority

If `Order.API` had to synchronously call each of those services, it would become tightly coupled to all of them. v6 avoids that by publishing an event instead.

### 2. Asynchronous messaging

RabbitMQ introduces a **brokered** communication style:

1. a service publishes a message
2. the broker stores/routes it
3. interested consumers handle it asynchronously

That matters because publishers and consumers no longer need to be online at the same instant in the same request chain. The publisher says, “this happened,” and the broker takes responsibility for delivery.

This is especially valuable in microservices because distributed systems are unreliable by nature:

- networks fail
- services restart
- consumers run slower than producers

A message broker helps absorb that uncertainty.

### 3. Loose coupling between services

The key architectural improvement in v6 is **decoupling by intent**.

Instead of saying:

- “Order service, call Catalog right now and tell it to reduce stock”

v6 says:

- “Order service, publish `OrderSubmittedEvent`”
- “Catalog service, if you care about orders, subscribe and react”

That is a major mindset shift.

The publisher does **not** need to know:

- how many consumers exist
- where they are deployed
- what database they use
- whether more consumers will appear later

It only needs to know the event contract.

### 4. Reliability through the outbox/inbox pattern

A common beginner mistake in microservices is to save data to a database and then publish an event as a separate best-effort step. If the process crashes between those two actions, the database change succeeds but the event is lost.

v6 explicitly avoids that problem.

Both `Order.API` and `Catalog.API` add MassTransit’s **EF Core outbox**, and `Catalog.API` also uses the **inbox** to protect consumers from duplicate delivery.

That means the architecture is not just “event-driven” in theory. It is trying to be **correct under failure**, which is what makes the change educationally important.

---

## What Changed

### 1. RabbitMQ infrastructure was added

The first big change is in Aspire orchestration. `AppHost` now provisions a RabbitMQ resource and wires services to it.

```csharp
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();
```

This was enabled by adding the Aspire hosting package:

```xml
<PackageReference Include="Aspire.Hosting.RabbitMQ" Version="13.3.5" />
```

#### Why this was added

RabbitMQ becomes the **event bus** for the system. Instead of services calling each other directly for every downstream reaction, they can publish events to the broker.

The management plugin matters for learners too: it gives visibility into queues, exchanges, and message flow during development. That makes the architecture easier to observe and debug.

#### Services now depend on the broker

In `AppHost.cs`, the relevant services gain a RabbitMQ reference and wait for it at startup:

```csharp
var catalog = builder.AddProject<Projects.SimpleStore_Catalog_API>("catalog")
    .WithReference(catalogDb)
    .WithReference(rabbitmq)
    .WaitFor(catalogDb)
    .WaitFor(rabbitmq);
```

The same pattern is applied to:

- `Order.API`
- `Catalog.API`
- `Cart.API`

This shows a useful Aspire idea: infrastructure is declared once in orchestration, then injected into the services that need it.

---

### 2. MassTransit was integrated into the services

v6 uses **MassTransit** as the application-level abstraction over RabbitMQ.

Package additions make that visible:

```xml
<PackageReference Include="MassTransit.RabbitMQ" Version="8.5.2" />
<PackageReference Include="MassTransit.EntityFrameworkCore" Version="8.5.2" />
```

MassTransit is important here because it gives SimpleStore:

- message publishing APIs
- consumer registration
- queue endpoint configuration
- EF Core outbox/inbox integration
- a consistent programming model across services

#### `Order.API`: publish-only integration

`Order.API` adds MassTransit with an EF Core outbox:

```csharp
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!));
        cfg.ConfigureEndpoints(ctx);
    });
});
```

This means `Order.API` can publish messages through `IPublishEndpoint`, but publication is backed by the outbox pattern instead of a fragile fire-and-forget network call.

#### `Catalog.API`: both publisher and consumer

`Catalog.API` integrates MassTransit in a richer way:

```csharp
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<CatalogDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.AddConsumer<OrderSubmittedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!));
        cfg.ConfigureEndpoints(ctx);
    });
});
```

So `Catalog.API` now plays two roles:

- **consumer** of `OrderSubmittedEvent`
- **publisher** of `ProductUpdatedEvent`

That is a realistic microservices pattern: a service can react to one business event and later emit another.

#### `Cart.API`: consumer-only integration

`Cart.API` adds MassTransit too, but only as a consumer:

```csharp
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ProductUpdatedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!));
        cfg.ConfigureEndpoints(ctx);
    });
});
```

This is educational because it shows that not every service needs the same messaging responsibilities.

- `Order.API` publishes facts
- `Catalog.API` publishes and consumes
- `Cart.API` only reacts

That variation is normal in microservices.

---

### 3. Shared event/message contracts were introduced

v6 adds a brand-new project:

- `SimpleStore.Contracts`

This project contains the integration event definitions shared across service boundaries.

#### Why a dedicated contracts project matters

In a distributed system, the message schema is part of the public contract between services. If those types live inside one service’s private code, reuse becomes awkward and versioning becomes harder to reason about.

By placing them in `SimpleStore.Contracts`, SimpleStore makes the event layer explicit.

The solution file was updated accordingly:

```xml
<Project Path="src/SimpleStore.Contracts/SimpleStore.Contracts.csproj" />
```

#### `OrderSubmittedEvent`

```csharp
public sealed record OrderSubmittedEvent
{
    public int OrderId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public decimal TotalAmount { get; init; }
    public string ShippingAddress { get; init; } = string.Empty;
    public IReadOnlyList<OrderSubmittedLineItem> Items { get; init; } = Array.Empty<OrderSubmittedLineItem>();
}
```

This event is intentionally richer than “just an order ID.”

Why? Because downstream services should not need to synchronously query `Order.API` just to do their work. Carrying line items and summary data inside the event reduces follow-up coupling.

#### `ProductUpdatedEvent`

```csharp
public sealed record ProductUpdatedEvent
{
    public int ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string ImageUrl { get; init; } = string.Empty;
    public int Stock { get; init; }
    public int CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
}
```

This message carries the denormalized product details that `Cart.API` needs in order to refresh cart lines.

Again, the design goal is to avoid a consumer immediately turning around and doing a synchronous HTTP read for the same data.

---

### 4. Which services publish and which consume events

v6 creates two concrete event flows.

#### Flow A: order submission updates catalog stock

**Publisher:** `Order.API`  
**Event:** `OrderSubmittedEvent`  
**Consumer:** `Catalog.API`

When an order is created, `Order.API` now publishes an event after persisting the order.

```csharp
await _publishEndpoint.Publish(new OrderSubmittedEvent
{
    OrderId = order.Id,
    UserId = order.UserId,
    OrderDate = order.OrderDate,
    TotalAmount = order.TotalAmount,
    ShippingAddress = order.ShippingAddress,
    Items = order.Items.Select(i => new OrderSubmittedLineItem
    {
        ProductId = i.ProductId,
        ProductName = i.ProductName,
        Quantity = i.Quantity,
        UnitPrice = i.UnitPrice
    }).ToList()
}, ct);
```

`Catalog.API` consumes that event in `OrderSubmittedConsumer`:

```csharp
foreach (var item in evt.Items)
{
    if (!products.TryGetValue(item.ProductId, out var product))
        continue;

    product.Stock -= item.Quantity;
}

await _context.SaveChangesAsync(ct);
```

#### Why this change was made

This replaces a tighter integration model with a looser one.

Instead of `Order.API` needing direct knowledge of Catalog’s API or database, it simply announces that an order was submitted. Catalog independently decides to decrement its stock cache.

That is a better microservices boundary because:

- Order owns order creation
- Catalog owns product read/write state
- messaging connects them without merging responsibilities

#### Flow B: product changes refresh carts

**Publisher:** `Catalog.API`  
**Event:** `ProductUpdatedEvent`  
**Consumer:** `Cart.API`

When a product is updated, `Catalog.API` now publishes a message after saving the new product data.

```csharp
await _publishEndpoint.Publish(new ProductUpdatedEvent
{
    ProductId = product.Id,
    Name = product.Name,
    Description = product.Description,
    Price = product.Price,
    ImageUrl = product.ImageUrl,
    Stock = product.Stock,
    CategoryId = product.CategoryId,
    CategoryName = product.Category?.Name ?? string.Empty
}, ct);
```

`Cart.API` then scans carts and updates any matching line items:

```csharp
await foreach (var ownerKey in _store.EnumerateOwnerKeysAsync(ct))
{
    ...
    foreach (var item in items)
    {
        if (item.ProductId != evt.ProductId) continue;
        item.ProductName = evt.Name;
        item.UnitPrice = evt.Price;
        item.ImageUrl = evt.ImageUrl;
        dirty = true;
    }
}
```

#### Why this change was made

Cart lines store **denormalized** product data such as name, price, and image URL. That improves cart read performance and keeps the cart service independent from Catalog at request time.

But denormalization creates a new responsibility: keeping copies fresh.

v6 solves that with an event:

- Catalog is the source of truth for product details
- Cart keeps local copies for fast reads
- `ProductUpdatedEvent` keeps those copies synchronized asynchronously

This is a classic event-driven denormalization pattern.

---

### 5. How synchronous HTTP calls were replaced with async messaging where appropriate

The important phrase is **“where appropriate.”** v6 does **not** remove HTTP from the system. It uses the right tool for each kind of interaction.

#### HTTP remains for commands and queries

The UI still uses synchronous HTTP for operations that need an immediate answer:

- browse catalog
- log in
- fetch cart contents
- create an order from the storefront

That is correct, because those are request/response scenarios.

#### Messaging is introduced for downstream reactions

What changed is the **follow-up work after a state change**.

Before v6, a beginner-friendly but tightly coupled design might have required:

- `Order.API` to call `Catalog.API` directly after saving an order
- `Cart.API` to call `Catalog.API` every time it needed refreshed product details

v6 moves those follow-up responsibilities to events instead:

- **order placed** → publish `OrderSubmittedEvent`
- **product updated** → publish `ProductUpdatedEvent`

This removes unnecessary synchronous chaining from the original request path.

That matters because synchronous chains have hidden costs:

- higher latency
- more coupling
- more cascading failures
- harder retry behavior

By moving secondary reactions to the broker, the original service can stay focused on its own transaction.

#### Important nuance for learners

Asynchronous messaging is not always a full replacement for HTTP.

In v6, it is used specifically for **integration events** — facts that other services may care about after the primary operation is complete.

That is a good rule of thumb:

- use **HTTP** when the caller needs an immediate response
- use **events/messages** when other services should react independently

---

### 6. Transactional consistency was improved with outbox/inbox support

This is one of the most important engineering details in the diff.

#### Order outbox

`OrderDbContext` was updated to include MassTransit persistence entities:

```csharp
builder.AddInboxStateEntity();
builder.AddOutboxMessageEntity();
builder.AddOutboxStateEntity();
```

And the migration creates supporting tables such as:

- `InboxState`
- `OutboxMessage`
- `OutboxState`

The order creation flow was also wrapped in an explicit transaction:

```csharp
await using var tx = await _context.Database.BeginTransactionAsync(ct);

_context.Orders.Add(order);
await _context.SaveChangesAsync(ct);
...
await _context.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

#### Why this matters

Without an outbox, a crash after writing the order but before publishing the event would leave the system inconsistent.

With the outbox:

- the order row and event record commit together
- a background dispatcher sends the message later
- temporary broker/network failures do not lose the event

That is a foundational reliability pattern in microservices.

#### Catalog inbox/outbox

`Catalog.API` gets both sides:

- **outbox** when publishing `ProductUpdatedEvent`
- **inbox** when consuming `OrderSubmittedEvent`

The inbox matters because message brokers often provide **at-least-once delivery**, not exactly-once delivery. If `OrderSubmittedEvent` were delivered twice and Catalog blindly decremented stock twice, the data would be wrong.

The inbox gives Catalog idempotent consumer protection at the framework/data level.

#### Cart takes a different approach

`Cart.API` has no EF Core DbContext, so it does not use MassTransit’s EF inbox/outbox.

Instead, the design chooses **idempotent handling**:

- re-processing the same `ProductUpdatedEvent` simply rewrites the same values
- quantities are untouched
- repeated delivery is therefore harmless

This is a great teaching point: different services can use different reliability mechanisms depending on their storage model.

---

### 7. Cart gained Redis key enumeration to support event fan-out

To react to `ProductUpdatedEvent`, `Cart.API` needed a way to find carts containing a product.

Instead of building a reverse index immediately, v6 adds a SCAN-based enumeration method:

```csharp
public async IAsyncEnumerable<string> EnumerateOwnerKeysAsync(...)
{
    foreach (var endpoint in _mux.GetEndPoints())
    {
        var server = _mux.GetServer(endpoint);
        await foreach (var key in server.KeysAsync(pattern: KeyPrefix + "*").WithCancellation(ct))
        {
            ...
        }
    }
}
```

This required:

- injecting `IConnectionMultiplexer`
- adding `Aspire.StackExchange.Redis`
- extending `ICartStore`

#### Why this design was chosen

For a small-to-medium cart count, scanning Redis keys is simple and keeps write logic uncomplicated.

The code comments make the trade-off explicit: if the cart count grows materially, the system should move to a maintained reverse index such as `product:{id}:carts`.

That is another valuable microservices lesson: sometimes a version chooses the **simplest architecture that is good enough now**, while clearly documenting the scaling limit.

---

### 8. Changes to Aspire orchestration

Aspire is the glue that makes the local distributed system easy to run.

In v6, orchestration changes do more than “start RabbitMQ.” They also document the architectural relationships.

`AppHost.cs` now tells the story of the system:

- `RabbitMQ` is the event bus
- `Order.API` publishes order events
- `Catalog.API` publishes and consumes product/order events
- `Cart.API` consumes product update events

Because the AppHost declares:

- service references
- infrastructure dependencies
- startup ordering with `WaitFor(...)`

learners can see both the **code-level** and **deployment-level** meaning of event-driven communication.

This is important in microservices: architecture is not only in service code. It also lives in orchestration.

---

## Architecture Diagram

```text
                         +----------------------+
                         |      SimpleStore     |
                         |        Web/UI        |
                         +----------+-----------+
                                    |
                                    | HTTP via Gateway
                                    v
+----------------+        +----------------------+        +----------------+
|  Order.API     |        |      RabbitMQ        |        |  Catalog.API   |
| owns orderdb   |------->|      event bus       |------->| owns catalogdb |
| publishes      |        |                      |        | consumes order |
| OrderSubmitted |        |                      |        | events         |
+----------------+        +----------------------+        | publishes      |
                                                          | ProductUpdated |
                                                          +--------+-------+
                                                                   |
                                                                   | event
                                                                   v
                                                          +----------------+
                                                          |   Cart.API     |
                                                          | owns Redis     |
                                                          | consumes       |
                                                          | ProductUpdated |
                                                          +----------------+
```

### Message flows

```text
1. Customer places order
   Web -> Gateway -> Order.API
   Order.API saves order -> publishes OrderSubmittedEvent
   RabbitMQ routes event -> Catalog.API consumes it
   Catalog.API decrements Product.Stock

2. Admin edits product
   Admin -> Gateway -> Catalog.API
   Catalog.API saves product -> publishes ProductUpdatedEvent
   RabbitMQ routes event -> Cart.API consumes it
   Cart.API refreshes denormalized product data in carts
```

### Conceptual lesson from the diagram

Notice that `Order.API` does **not** call `Catalog.API` directly, and `Catalog.API` does **not** call `Cart.API` directly for this integration behavior.

The broker sits in the middle, which means:

- publishers are simpler
- consumers are optional and replaceable
- new subscribers can be added later with less impact

That is the architectural value of message-based integration.

---

## Key Takeaways

1. **Events model business facts.**  
   `OrderSubmittedEvent` and `ProductUpdatedEvent` represent things that happened, not remote procedure calls in disguise.

2. **Microservices should not couple every follow-up action to HTTP.**  
   Request/response is useful, but downstream reactions are often better handled asynchronously.

3. **A message broker is an architectural boundary.**  
   RabbitMQ decouples publishers from consumers in time and in topology.

4. **Shared contracts are critical.**  
   `SimpleStore.Contracts` makes event schemas explicit and reusable.

5. **Reliability patterns matter as much as messaging itself.**  
   The outbox/inbox additions are what make the design production-minded instead of demo-only.

6. **Denormalization often pairs naturally with events.**  
   `Cart.API` keeps local product snapshots, and events keep them fresh.

7. **Different services can choose different consistency strategies.**  
   EF-backed services use outbox/inbox tables; Redis-backed Cart uses idempotent consumer logic.

8. **Event-driven architecture is additive, not absolute.**  
   v6 does not remove HTTP; it introduces messaging where it improves boundaries and resilience.

---

## Trade-offs

### Pros

#### Better decoupling

Publishers do not need to know their subscribers. That makes the system easier to evolve.

#### More scalable integration model

New consumers can be added later without changing the publisher. For example, analytics or email notifications could subscribe to order events in a future version.

#### Better resilience

Temporary consumer outages do not necessarily break the original request. The broker and outbox absorb some failure scenarios.

#### More natural fit for cross-service side effects

Stock updates and denormalized cart refreshes are examples of side effects that do not need to happen synchronously in the original request.

### Cons

#### More operational complexity

The system now needs RabbitMQ in addition to Postgres and Redis. Developers must understand queues, consumers, retries, and broker health.

#### Eventual consistency

After an order is created, Catalog stock is not updated in the same synchronous transaction across both services. There can be a short delay before consumers apply the event.

#### Harder debugging

Tracing a workflow across multiple services and a broker is more complex than stepping through one HTTP request.

#### Schema/version management becomes important

Once events are shared contracts, changing them carelessly can break consumers.

#### Some patterns are only “good enough for now”

The Redis SCAN strategy in `Cart.API` is simple, but it may not scale forever. Event-driven systems often require these kinds of staged design decisions.

---

## Final Learning Summary

v6 is a strong teaching version because it demonstrates that microservices are not only about splitting code into separate projects. They are also about choosing the right **integration style**.

This version moves SimpleStore from:

- service boundaries defined mostly by HTTP APIs

to:

- service boundaries connected by both **HTTP** and **events**, each used for the job it fits best

That makes the architecture more realistic, more decoupled, and more aligned with how many production microservice systems evolve over time.
