# v5 Changes — Add API Gateway and Refactor Service Routing

## Overview

Version 5 introduces **`SimpleStore.Gateway`** as the system's new **single HTTP entry point** for the UI applications.

Before v5:

- `SimpleStore.Web` talked directly to backend services like `identity`, `catalog`, `order`, and `cart`
- `SimpleStore.Admin` also talked directly to multiple backend services
- each client needed to know which service to call and which URL shape to use

After v5:

- `Web` and `Admin` talk to **one service: the gateway**
- the gateway routes requests to the correct backend service
- route rules and authorization can now be centralized at the edge
- the system gets a cleaner external API shape: `/api/v1/<service>/...`

This is a classic **API Gateway** step in a microservices architecture. Once a system has several backend services, it becomes valuable to add a front door that hides internal topology and gives clients a stable entry point.

---

## Why This Matters

### API Gateway pattern

As microservices multiply, clients can become tightly coupled to the internal service landscape.

Without a gateway, clients need to know:

- which services exist
- where each service lives
- which paths each service exposes
- which routes require authentication or admin privileges

That creates a lot of **distributed coupling in the clients**.

The **API Gateway pattern** solves that by placing a reverse proxy in front of the services. The gateway becomes responsible for:

- receiving incoming requests
- matching them to routes
- forwarding them to the correct backend
- optionally enforcing cross-cutting concerns such as authentication, authorization, logging, rate limiting, or versioned URL structure

### Single entry point

A single entry point matters because it gives the system a cleaner boundary.

Instead of this mental model:

> The UI needs to know the whole microservice map.

v5 moves to this model:

> The UI talks to one front door, and the gateway knows the map.

That is important for learners because it shows a common progression in microservices:

1. **extract services**
2. **have clients call them directly**
3. **add a gateway when the number of services and routing rules grows**

### Centralized edge policies

v5 does **not** remove authorization from the backend services. Instead, it adds another enforcement layer at the edge.

That is a strong architectural choice:

- the gateway can reject obviously unauthorized requests earlier
- the backend services still protect themselves
- policy is enforced in **defense in depth**, not in only one place

So the gateway improves client simplicity **without turning backend services into insecure internal-only components**.

---

## What Changed

### 1. A new `SimpleStore.Gateway` project was added

The commit adds a brand new ASP.NET Core project:

- `src/SimpleStore.Gateway/Program.cs`
- `src/SimpleStore.Gateway/appsettings.json`
- `src/SimpleStore.Gateway/appsettings.Development.json`
- `src/SimpleStore.Gateway/SimpleStore.Gateway.csproj`
- `src/SimpleStore.Gateway/Properties/launchSettings.json`

Its package references show exactly what kind of gateway this is:

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.8" />
<PackageReference Include="Microsoft.Extensions.ServiceDiscovery.Yarp" Version="10.6.0" />
<PackageReference Include="Yarp.ReverseProxy" Version="2.3.0" />
```

That tells us two important things:

1. the gateway is built with **YARP** (Yet Another Reverse Proxy)
2. it integrates with **Aspire service discovery**, so destinations can be expressed as service names like `https+http://catalog`

### Why YARP?

YARP is a good fit here because it lets the team add a gateway **without writing a lot of custom proxy code**. Instead of building a router by hand, the application can declare routes in configuration and let YARP handle forwarding.

That keeps the gateway focused on architectural concerns:

- route mapping
- auth/authz at the edge
- stable public URL design

### Gateway startup behavior

The new `Program.cs` is intentionally small:

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(
                string.IsNullOrEmpty(jwtKey) ? new byte[32] : Convert.FromBase64String(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });
```

```csharp
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();
```

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.MapReverseProxy();
```

This is educationally important because it shows the gateway doing three jobs:

- **validating JWTs**
- **applying authorization policies**
- **proxying requests based on configuration**

The code is small because the behavior lives mostly in configuration. That is often how production gateways are designed: policy-heavy, not business-logic-heavy.

---

### 2. Routing was refactored to go through the gateway

The biggest behavioral change in v5 is that the UI apps stop talking to backend services individually.

#### AppHost before and after

In `AppHost.cs`, `Web` used to reference multiple backend services directly. The diff replaces that with a reference to the new gateway:

```csharp
var gateway = builder.AddProject<Projects.SimpleStore_Gateway>("gateway")
    .WithReference(identity)
    .WithReference(catalog)
    .WithReference(order)
    .WithReference(cart)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(identity)
    .WaitFor(catalog)
    .WaitFor(order)
    .WaitFor(cart);
```

```csharp
var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(gateway)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(gateway);
```

```csharp
var admin = builder.AddProject<Projects.SimpleStore_Admin>("admin")
    .WithReference(gateway)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(gateway);
```

### Why this refactor matters

This changes the dependency shape of the whole system.

Before v5, Web/Admin effectively needed direct awareness of multiple backend services. After v5, they only need awareness of one service: the gateway.

That gives several architectural benefits:

- **simpler clients**: fewer direct service references
- **hidden topology**: internal service layout can evolve without changing every client
- **centralized policy**: routing and edge authorization move into one place
- **consistent API surface**: clients use one URL convention instead of many internal ones

This is a textbook example of reducing **client-to-service coupling** in a microservices system.

---

### 3. Route mappings were declared in gateway configuration

The gateway's `appsettings.json` is the core of the change. It defines:

- **routes**: what incoming URL patterns the gateway accepts
- **clusters**: which backend service each route forwards to
- **transforms**: how the public path is rewritten into the backend path
- **authorization policy**: whether the route is anonymous, authenticated, or admin-only

#### Example: identity routes

```json
"identity-anon-login": {
  "ClusterId": "identity-cluster",
  "Match": { "Path": "/api/v1/identity/login", "Methods": [ "POST" ] },
  "Transforms": [ { "PathPattern": "/api/identity/login" } ]
}
```

```json
"identity-admin-users": {
  "ClusterId": "identity-cluster",
  "AuthorizationPolicy": "Admin",
  "Match": { "Path": "/api/v1/identity/users/{**catch-all}" },
  "Transforms": [ { "PathPattern": "/api/identity/users/{**catch-all}" } ]
}
```

This teaches an important lesson: a gateway can expose **different access rules for different parts of the same service**.

- login/register stay anonymous
- `/users/*` becomes admin-only
- the rest of identity becomes authenticated-only

#### Example: catalog routes

```json
"catalog-read": {
  "ClusterId": "catalog-cluster",
  "Match": { "Path": "/api/v1/catalog/{**catch-all}", "Methods": [ "GET", "HEAD" ] },
  "Transforms": [ { "PathPattern": "/api/catalog/{**catch-all}" } ]
},
"catalog-write": {
  "ClusterId": "catalog-cluster",
  "AuthorizationPolicy": "Admin",
  "Match": { "Path": "/api/v1/catalog/{**catch-all}", "Methods": [ "POST", "PUT", "DELETE", "PATCH" ] },
  "Transforms": [ { "PathPattern": "/api/catalog/{**catch-all}" } ]
}
```

This is a nice example of **method-aware routing policy**:

- reads are open
- writes are protected

That mirrors the service's own intent but moves a first layer of enforcement to the edge.

#### Example: order and cart routes

```json
"order-admin": {
  "ClusterId": "order-cluster",
  "AuthorizationPolicy": "Admin",
  "Match": { "Path": "/api/v1/order/admin/{**catch-all}" },
  "Transforms": [ { "PathPattern": "/api/order/admin/{**catch-all}" } ]
},
"order-user": {
  "ClusterId": "order-cluster",
  "AuthorizationPolicy": "AuthenticatedUser",
  "Match": { "Path": "/api/v1/order/{**catch-all}" },
  "Transforms": [ { "PathPattern": "/api/order/{**catch-all}" } ]
}
```

```json
"cart-merge": {
  "ClusterId": "cart-cluster",
  "AuthorizationPolicy": "AuthenticatedUser",
  "Match": { "Path": "/api/v1/cart/merge", "Methods": [ "POST" ] },
  "Transforms": [ { "PathPattern": "/api/cart/merge" } ]
},
"cart-any": {
  "ClusterId": "cart-cluster",
  "Match": { "Path": "/api/v1/cart/{**catch-all}" },
  "Transforms": [ { "PathPattern": "/api/cart/{**catch-all}" } ]
}
```

This highlights another gateway lesson: **public URLs do not have to match internal URLs exactly**. The gateway can present a versioned contract (`/api/v1/...`) while forwarding to the current internal service paths (`/api/...`).

### Why the path transform matters

The transform layer is subtle but very important. The gateway standardizes the public API surface like this:

- public: `/api/v1/catalog/products`
- internal: `/api/catalog/products`

That gives the system room to evolve:

- future versions can add `/api/v2/...`
- internal services do not need to change immediately
- clients get a more intentional, versioned API shape

---

### 4. Backend destinations were expressed as service-discovered clusters

The `Clusters` section maps logical route groups to concrete backend services:

```json
"identity-cluster": {
  "Destinations": {
    "primary": { "Address": "https+http://identity" }
  }
},
"catalog-cluster": {
  "Destinations": {
    "primary": { "Address": "https+http://catalog" }
  }
}
```

```json
"order-cluster": {
  "Destinations": {
    "primary": { "Address": "https+http://order" }
  }
},
"cart-cluster": {
  "Destinations": {
    "primary": { "Address": "https+http://cart" }
  }
}
```

This matters because the gateway is not hard-coding local ports. It is using **service discovery names** provided by Aspire.

That is a better microservices practice than wiring every client to specific ports because:

- environments can differ
- service instances can move
- deployment becomes more flexible
- clients depend on logical service identity, not on network trivia

---

### 5. Web and Admin now communicate through the gateway

The clearest evidence of this change is in the typed client libraries.

Each client extension changes its default target service from the backend service name to `gateway`.

#### Catalog client extension

```csharp
public static IHttpClientBuilder AddCatalogApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "gateway")
```

#### Identity client extension

```csharp
public static IHttpClientBuilder AddIdentityApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "gateway")
```

#### Order client extension

```csharp
public static IHttpClientBuilder AddOrderApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "gateway")
```

#### Cart client extension

```csharp
public static IHttpClientBuilder AddCartApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "gateway")
```

That change is small in code, but big in architecture. It means the UI code can keep using typed clients, while the network hop behind those clients changes from:

- **UI -> service**

to:

- **UI -> gateway -> service**

### URL changes in the clients

The client methods were also updated to call the gateway's versioned route scheme.

Examples from the diff:

```csharp
// Catalog client
var query = $"api/v1/catalog/products?page={page}&pageSize={pageSize}";
```

```csharp
// Identity client
using var response = await _http.PostAsJsonAsync("api/v1/identity/login", request, cancellationToken);
```

```csharp
// Order client
using var response = await _http.PostAsJsonAsync("api/v1/order/orders", request, cancellationToken);
```

```csharp
// Cart client
var result = await _http.GetFromJsonAsync<CartDto>("api/v1/cart", cancellationToken);
```

### Why this is a strong design choice

This preserves a clean layering model:

- UI code still depends on typed client abstractions
- typed clients now speak the gateway's public contract
- backend topology is hidden behind the gateway

So v5 improves architecture **without forcing controller/page/component code to know about proxy mechanics**.

---

### 6. Aspire orchestration was updated to include the gateway

The AppHost project file now references the new gateway project:

```xml
<ProjectReference Include="..\SimpleStore.Gateway\SimpleStore.Gateway.csproj" />
```

And the orchestration graph changes in a meaningful way:

- `gateway` depends on `identity`, `catalog`, `order`, and `cart`
- `web` depends on `gateway`
- `admin` depends on `gateway`

### Why this matters in Aspire

Aspire is not just launching processes here. It is expressing the **runtime dependency graph**.

That matters because it documents the intended architecture in executable form:

- backend services sit behind the gateway
- UI projects should not bypass the gateway
- startup ordering reflects the new communication path

This is a good lesson for learners: orchestration code is part of the architecture, not just deployment plumbing.

---

## Architecture Diagram

```text
                 +----------------------+            +----------------------+
                 |   SimpleStore.Web    |            |  SimpleStore.Admin   |
                 +----------+-----------+            +----------+-----------+
                            |                                   |
                            +----------------+------------------+
                                             |
                                             v
                                  +----------------------+
                                  |  SimpleStore.Gateway |
                                  |   /api/v1/...        |
                                  |  YARP reverse proxy  |
                                  |  JWT auth at edge    |
                                  +----+----+----+-------+
                                       |    |    |    |
                                       v    v    v    v
                              +--------+ +--+----+ +--+----+ +--------+
                              |Identity| |Catalog | |Order   | |Cart    |
                              |API     | |API     | |API     | |API     |
                              |/api/   | |/api/   | |/api/   | |/api/   |
                              |identity| |catalog | |order   | |cart    |
                              +--------+ +--------+ +--------+ +--------+
```

A simpler request flow looks like this:

```text
Browser/UI -> Web or Admin -> Gateway -> Target backend service
```

Examples:

```text
Web -> /api/v1/catalog/products  -> Gateway -> Catalog.API /api/catalog/products
Web -> /api/v1/cart              -> Gateway -> Cart.API    /api/cart
Admin -> /api/v1/order/admin/*   -> Gateway -> Order.API   /api/order/admin/*
Web -> /api/v1/identity/login    -> Gateway -> Identity.API /api/identity/login
```

---

## Key Takeaways

1. **An API Gateway is a natural next step once several microservices exist.**  
   It prevents clients from having to understand the whole backend layout.

2. **A gateway gives the system a stable public API surface.**  
   In v5, `/api/v1/<service>/...` becomes the external shape even though the internal services still use `/api/<service>/...`.

3. **Edge authorization complements service authorization.**  
   The gateway enforces route policies early, but backend services still keep their own protections.

4. **Configuration-driven routing is powerful.**  
   YARP lets route matching, path transforms, and auth policy live in config instead of scattered custom code.

5. **Clients become simpler when topology is hidden.**  
   Web and Admin no longer need direct references to every backend service.

6. **Service discovery and gateways work well together.**  
   The gateway routes to logical service names such as `https+http://catalog`, which is cleaner than hard-coded ports.

---

## Trade-offs

### Benefits

#### 1. Simpler client architecture

`Web` and `Admin` now have one backend entry point. That reduces direct dependencies and makes the UI side easier to reason about.

#### 2. Centralized routing and policy

Instead of duplicating route knowledge across clients, the gateway owns it in one place.

#### 3. Better API consistency

The versioned `/api/v1/...` surface gives a clearer external contract and creates room for future API evolution.

#### 4. Better operational control

A gateway becomes a natural place to later add:

- rate limiting
- observability
- request/response logging
- correlation IDs
- caching
- circuit breaking or specialized routing

### Costs and risks

#### 1. Extra network hop

Requests now travel through one more component:

- UI -> gateway -> service

That adds some latency and another place where failures can occur.

#### 2. More infrastructure to maintain

A gateway is another deployable service, another configuration surface, and another thing to monitor.

#### 3. Risk of policy drift

If the gateway policy and backend policy diverge, behavior can become confusing. That is why keeping backend authorization in place is important.

#### 4. Gateway can become a bottleneck

If too much logic gets pushed into the gateway, it can turn into a mini-monolith. v5 avoids that by keeping the gateway focused on routing and auth, not business logic.

#### 5. Route configuration grows over time

As services and endpoints expand, the gateway config can become large. Teams need naming conventions and discipline to keep route definitions understandable.

---

## Final Summary

v5 is the version where SimpleStore stops exposing its growing set of microservices directly to the UI and introduces a **proper front door**.

The most important architectural lesson is this:

> microservices are not only about splitting code into services; they are also about managing how clients enter the system.

By adding `SimpleStore.Gateway`, SimpleStore makes the architecture more realistic and more scalable:

- clients talk to one entry point
- routing is centralized
- authorization can be enforced at the edge
- internal services remain independently owned behind the gateway

For learners, v5 is a strong example of how a system evolves from **direct service-to-client communication** toward a more mature **gateway-based microservices architecture**.
