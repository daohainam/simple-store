# v4 Changes — Extract `OrderService` and `CartService`

## Overview

Version 4 is the step where **ordering** and **shopping cart** stop being internal features of `SimpleStore.Web` and become **separate backend services**:

- `SimpleStore.Order.API` becomes the owner of order workflows and `orderdb`
- `SimpleStore.Cart.API` becomes the owner of cart workflows and Redis-backed cart state
- `SimpleStore.Web` and `SimpleStore.Admin` stop reaching into order storage directly and start calling services over HTTP

This is an important microservices milestone because the system moves from “a web app with some extracted APIs” to a more explicit **service-based architecture**, where each capability owns its own code and data.

---

## Why This Matters

### 1. Further decomposition

Earlier versions had already extracted **Identity** and **Catalog**. In v4, the same idea is applied again to **Orders** and **Cart**.

That matters because microservices are not just about having many projects. They are about identifying **business capabilities** and giving each one a clearer boundary. In an e-commerce system, cart and order processing are both strong candidates for their own services:

- **Cart** has different storage needs and lifecycle behavior than the rest of the system
- **Order** has business rules, reporting needs, and admin workflows that deserve their own API surface

### 2. Single responsibility

Before v4, `SimpleStore.Web` was doing too much:

- rendering HTML
- handling authentication state
- storing shopping cart state in session
- writing orders directly to the database

That mixes **presentation concerns** with **business/data concerns**.

After v4:

- `Web` focuses on the storefront UI and user flow
- `Admin` focuses on administrative UI
- `Order.API` focuses on order behavior and persistence
- `Cart.API` focuses on cart behavior and storage

This is a classic microservices lesson: **the UI should consume services, not secretly act like the service**.

### 3. Service-owned data

Microservices work best when each service owns its own persistence. v4 makes that much more explicit:

- `Order.API` owns `orderdb`
- `Cart.API` owns Redis cart storage
- `Web` no longer migrates or queries order data directly
- `Admin` no longer injects `OrderDbContext`

This is one of the most important architectural shifts in the diff.

---

## What Changed

### 1. New `Order.API` project

`SimpleStore.Order.API` is introduced as a real ASP.NET Core Web API project. It contains:

- `Program.cs`
- `Endpoints/OrderEndpoints.cs`
- `Services/IOrderService.cs`
- `Services/OrderService.cs`
- `Data/OrderDbContext.cs`
- `Models/Order.cs`, `OrderItem.cs`
- migrations and startup migration/seeding logic

The project is no longer just a shared data library. It becomes a **service with its own HTTP contract**.

A key part of the startup code shows the new service boundary:

```csharp
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb");
builder.Services.AddScoped<IOrderService, OrderService>();
...
app.UseAuthentication();
app.UseAuthorization();
app.MapOrderEndpoints();
```

That tells learners something important: the order capability now has its own:

- process
- HTTP endpoints
- dependency injection container
- authentication/authorization rules
- database ownership

#### Order API surface

The diff adds two kinds of endpoints:

- **storefront endpoints** for the current user under `/api/order/orders`
- **admin endpoints** under `/api/order/admin/orders`

Example from the diff:

```csharp
var orders = group.MapGroup("/orders").RequireAuthorization();
...
var admin = group.MapGroup("/admin/orders").RequireAuthorization("Admin");
```

This is educationally useful because it shows how a service can expose **multiple consumers**:

- the customer-facing site
- the admin dashboard

while still centralizing the business rules in one place.

#### Order code moved out of Web

The old `SimpleStore.Web/Services/OrderService.cs` was deleted. Previously, Web created orders directly through `OrderDbContext`:

```csharp
_context.Orders.Add(order);
await _context.SaveChangesAsync();
```

That code now lives inside `SimpleStore.Order.API/Services/OrderService.cs` instead.

This is the core decomposition move: **the business operation did not disappear; it moved to the service that owns it**.

---

### 2. New `Cart.API` project

`SimpleStore.Cart.API` is also introduced as its own service. Its major pieces are:

- `Program.cs`
- `Endpoints/CartEndpoints.cs`
- `Services/ICartStore.cs`
- `Services/RedisCartStore.cs`

The most important architectural idea here is that **cart storage is not relational**. Instead of forcing cart data into the same model as orders, v4 gives cart a storage mechanism that fits its behavior.

The service is wired like this:

```csharp
builder.AddRedisDistributedCache("cart-redis");
builder.Services.AddScoped<ICartStore, RedisCartStore>();
```

That is a good microservices lesson: once a capability becomes its own service, it can choose the storage technology that fits its needs.

#### Why Redis for carts?

A shopping cart is usually:

- temporary
- frequently updated
- easy to model as a document/list
- not a long-term system of record like an order

Redis is a natural fit for that. The diff stores cart items as JSON with sliding expiration:

```csharp
private static readonly DistributedCacheEntryOptions EntryOptions = new()
{
    SlidingExpiration = TimeSpan.FromDays(30)
};
```

This replaces the older in-process/session-based cart behavior from `SimpleStore.Web/Services/CartService.cs`.

#### Anonymous and authenticated cart support

One of the most interesting design choices in v4 is how the cart service supports both anonymous and logged-in users.

From the diff:

```csharp
var sub = ctx.User.FindFirst("sub")?.Value;
if (!string.IsNullOrEmpty(sub)) return $"user:{sub}";

var anon = ctx.Request.Headers[CartIdHeader].ToString();
return string.IsNullOrEmpty(anon) ? null : $"anon:{anon}";
```

This means the cart service can identify a cart by:

- JWT `sub` claim for authenticated users
- `X-Cart-Id` header for anonymous users

That is a practical example of designing a service API around real UX requirements.

#### Cart merge on login

The diff also adds `CartMergeMiddleware` in Web so an anonymous cart can be folded into the user cart after login.

```csharp
await cart.MergeAsync(anonCartId, context.RequestAborted);
cookies.Clear();
```

This is a great teaching example because it shows that decomposition often creates **new integration logic**. Once cart is its own service, the application needs an explicit workflow for transitioning from anonymous identity to authenticated identity.

---

### 3. HTTP client patterns for inter-service communication

A major v4 theme is replacing **in-process calls** and **shared DbContext access** with **typed HTTP clients**.

#### New client libraries

Two client projects are added:

- `SimpleStore.Order.API.Client`
- `SimpleStore.Cart.API.Client`

These libraries contain:

- request/response DTOs
- interfaces like `IOrderApiClient` and `ICartApiClient`
- concrete `HttpClient` implementations
- registration helpers for Aspire service discovery

Example registration pattern:

```csharp
public static IHttpClientBuilder AddOrderApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "order")
{
    return builder.Services.AddHttpClient<IOrderApiClient, OrderApiClient>(client =>
    {
        client.BaseAddress = new Uri($"https+http://{serviceName}");
    });
}
```

This pattern matters because it gives the codebase a repeatable way to consume services:

1. define DTOs in a client package
2. wrap HTTP calls in a typed client
3. inject the typed client into UI code
4. let Aspire resolve service addresses

That is much cleaner than scattering raw `HttpClient` calls everywhere.

#### Web switches to service calls

In `SimpleStore.Web/Program.cs`, the app stops registering local cart/order services and starts registering HTTP clients:

```csharp
builder.AddOrderApiClient().AddHttpMessageHandler<BearerTokenHandler>();
builder.AddCartApiClient()
    .AddHttpMessageHandler<BearerTokenHandler>()
    .AddHttpMessageHandler<CartIdHandler>();
```

This is especially educational because it shows two important middleware-like outbound concerns:

- `BearerTokenHandler` adds the current JWT for authenticated service-to-service calls
- `CartIdHandler` adds `X-Cart-Id` when the shopper is anonymous

So the Web app becomes a proper **BFF/client of services**, rather than the owner of order/cart logic.

#### Controllers become thinner

`CartController` and `OrdersController` are rewritten to call APIs instead of local services.

Before, Web relied on interfaces like `ICartService` and `IOrderService` backed by local implementations.

After v4, it does things like:

```csharp
var cart = await _cart.GetAsync();
var order = await _orders.CreateOrderAsync(request);
await _cart.ClearAsync();
```

This is exactly what learners should notice in a decomposition diff: **controllers become orchestration code, not data-access code**.

#### Admin switches too

The admin app also stops talking directly to `OrderDbContext`.

Examples from the diff:

```csharp
@inject IOrderApiClient OrderApi
```

```csharp
var orderCounts = await OrderApi.GetOrderCountsByUserAsync();
```

```csharp
stats = await Orders.GetStatsAsync();
```

```csharp
await OrderApi.UpdateOrderStatusAsync(row.Id, row.Status);
```

This matters because it reinforces a core microservices rule: **even internal dashboards should go through service APIs**, not bypass them and touch another service's data store directly.

---

### 4. Aspire orchestration changes

The AppHost is updated so `Order.API` and `Cart.API` become first-class parts of the distributed application.

#### New infrastructure

Redis is added for carts:

```csharp
var cartRedis = builder.AddRedis("cart-redis")
    .WithRedisInsight();
```

This shows how orchestration evolves as the architecture evolves. Once cart becomes a service, the platform must provide its runtime dependency too.

#### New service registrations

The AppHost now starts `order` and `cart` explicitly:

```csharp
var order = builder.AddProject<Projects.SimpleStore_Order_API>("order")
    .WithReference(orderDb)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(orderDb);

var cart = builder.AddProject<Projects.SimpleStore_Cart_API>("cart")
    .WithReference(cartRedis)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(cartRedis);
```

This is more than wiring. It documents the system's new boundaries:

- `order` depends on `orderdb`
- `cart` depends on Redis
- both validate JWTs

#### Web/Admin depend on services, not databases

One of the clearest architectural signals in the diff is this change:

```csharp
.WithReference(orderDb)
```

becomes

```csharp
.WithReference(order)
.WithReference(cart)
```

for Web, and Admin similarly switches from `orderDb` to `order`.

That is exactly what you want to see in a microservices architecture. Consumers should depend on **service endpoints**, not another team's database.

---

### 5. Data ownership changes

This is one of the most important v4 lessons.

#### `SimpleStore.Data` stops being the shared order home

The diff removes the old shared data project from the solution and moves order artifacts into `SimpleStore.Order.API`:

- `OrderDbContext`
- `Order` / `OrderItem`
- migrations
- startup migration logic

The rename is visible directly in the diff:

```diff
-namespace SimpleStore.Data;
+namespace SimpleStore.Order.API.Data;
```

and:

```diff
-namespace SimpleStore.Data.Models;
+namespace SimpleStore.Order.API.Models;
```

This is the architectural meaning of “service owns its data.”

The schema, model, migrations, and business logic now live together in the service that owns the capability.

#### Web no longer owns order database lifecycle

The old Web startup used to migrate `orderdb` directly. That code is removed:

```csharp
using (var scope = app.Services.CreateScope())
{
    var orderDb = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await orderDb.Database.MigrateAsync();
}
```

Now `Order.API` does its own startup migration:

```csharp
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await OrderSeeder.SeedAsync(context);
}
```

This is a subtle but critical improvement. If Web were still migrating order storage, then Web would still be acting like part of the order service.

#### Cart leaves ASP.NET session storage

The old cart was stored in session under a key like `shopping_cart`. That entire service is deleted.

So v4 also demonstrates that data ownership is not only about databases. It is about **where state lives and who is allowed to manage it**.

Before:

- cart state lived inside the Web app process/session model

After:

- cart state lives in Cart.API's Redis store
- Web only sends commands and displays results

---

## Architecture Diagram

### Before v4

```text
                +-------------------+
                |   SimpleStore.Web |
                |  MVC + Razor UI   |
                |  Cart logic       |
                |  Order logic      |
                +---------+---------+
                          |
                +---------v---------+
                | shared orderdb /  |
                | session cart state|
                +-------------------+

Other extracted services already existed:
- Identity.API
- Catalog.API
```

### After v4

```text
                              +----------------------+
                              |  SimpleStore.AppHost |
                              |   Aspire orchestration|
                              +----------+-----------+
                                         |
         -----------------------------------------------------------------
         |                 |                  |                |           |
         v                 v                  v                v           v
+----------------+ +----------------+ +----------------+ +-----------+ +----------------+
| Identity.API   | | Catalog.API    | | Order.API      | | Cart.API  | | SimpleStore.Web|
| owns identitydb| | owns catalogdb | | owns orderdb   | | owns Redis| | storefront BFF |
+----------------+ +----------------+ +----------------+ +-----------+ +----------------+
         ^                 ^                  ^                ^                  |
         |                 |                  |                |                  |
         |                 |                  |                |      typed HTTP clients
         |                 |                  |                |      + auth/cart handlers
         |                 |                  |                |                  |
         |                 |                  |                |                  v
         |                 |                  |                |        +----------------+
         |                 |                  +----------------+--------| SimpleStore.Admin|
         |                 |                           admin/order calls| admin BFF        |
         |                 |                                            +----------------+
```

### Request flow examples

**Checkout flow**

```text
Browser -> Web -> Cart.API (read cart)
Browser -> Web -> Order.API (create order)
Browser <- Web
```

**Anonymous cart flow**

```text
Browser -- ss_cart cookie --> Web -- X-Cart-Id --> Cart.API -- Redis
```

**Admin order update flow**

```text
Admin UI -> Admin -> Order.API -> orderdb
```

---

## Key Takeaways

1. **Extracting a service means moving both logic and data ownership.**
   It is not enough to create a new API project. v4 also moves `DbContext`, models, migrations, and startup ownership into `Order.API`.

2. **UIs should consume services, not bypass them.**
   Both `Web` and `Admin` stop talking directly to order storage and instead use typed HTTP clients.

3. **Different capabilities can use different storage technologies.**
   Orders stay relational in Postgres; carts move to Redis because their access pattern is different.

4. **Decomposition often creates integration work.**
   The anonymous-cart cookie, `X-Cart-Id` header, and merge-on-login middleware are all examples of the extra glue code needed when boundaries become explicit.

5. **Typed client libraries reduce friction.**
   The new `*.API.Client` projects make service consumption consistent and help keep HTTP details out of controllers and pages.

6. **Service boundaries become visible in orchestration.**
   AppHost changes are not just infrastructure noise; they are a concrete map of the architecture.

---

## Trade-offs

### Benefits

- **Clearer ownership**: orders belong to `Order.API`, carts belong to `Cart.API`
- **Better separation of concerns**: UI code becomes thinner and easier to reason about
- **Independent evolution**: cart can evolve separately from order processing
- **Technology fit**: Redis is a better match for cart data than server session or a relational schema
- **More realistic architecture**: internal consumers must use the same APIs as external-facing app components

### Costs

- **More moving parts**: more projects, more services, more startup dependencies
- **Network boundaries**: calls that were once in-process become HTTP calls
- **Operational complexity**: AppHost now has to orchestrate Redis and two additional services
- **More integration code**: auth propagation, cart identity propagation, and cart merge logic are all new concerns
- **Eventual refactoring overhead**: DTOs, typed clients, and service contracts add upfront structure that a monolith would not need yet

### The main lesson

Microservices improve modularity by making boundaries explicit, but explicit boundaries are never free. v4 is a good teaching version because it shows both sides honestly:

- the architecture becomes cleaner
- the runtime and integration story becomes more complex

That trade-off is normal. The goal is not “maximum number of services,” but **better alignment between business capabilities, code ownership, and data ownership**.
