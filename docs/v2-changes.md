# v2 Changes: Extract Catalog to a Microservice with an HTTP API Client

## Overview

Version 2 is the point where **Catalog stops being an in-process module** and becomes its own deployable service: `SimpleStore.Catalog.API`.

Before v2, both **Web** and **Admin** talked to catalog data by referencing `CatalogDbContext` directly. After v2, they stop reaching into the database model and instead call a **catalog HTTP API** through a shared typed client library, `SimpleStore.Catalog.API.Client`.

This version applies two foundational microservices ideas:

- **Service extraction**: move one business capability into its own service boundary.
- **API-first design**: consumers talk to the service through a stable contract, not through EF Core entities or direct database access.

In short, v2 turns Catalog into the system's **first true application service**.

---

## Why This Matters

When a UI project directly uses another module's `DbContext`, it is tightly coupled to that module's database schema, EF queries, and internal entity design.

That works in a monolith, but it makes service extraction hard because:

- multiple apps depend on the same database shape
- changes to tables/entities ripple into multiple projects
- ownership is unclear: who is allowed to change catalog data?
- the database becomes the integration point instead of the service contract

v2 fixes that by making **Catalog own its data and expose capabilities over HTTP**.

### Microservices principle: service extraction

Catalog becomes a boundary around one business capability:

- products
- categories
- catalog queries
- catalog CRUD operations
- catalog schema, migrations, and seed data

That is important because a microservice should own its:

1. **data**
2. **business logic**
3. **integration contract**

### Microservices principle: API-first design

Instead of saying:

> "Web can query the Catalog tables however it wants"

v2 says:

> "Web and Admin must ask Catalog for data through the Catalog API"

That is a major architectural shift. It forces the system to define:

- explicit request/response models (`ProductDto`, `CategoryDto`, `PagedResult<T>`)
- explicit operations (`GetProductsAsync`, `CreateProductAsync`, etc.)
- explicit network boundaries (HTTP)

This makes later evolution much easier: authentication, versioning, caching, retries, gateways, and independent deployments all become more natural once the service boundary exists.

---

## What Changed

## 1) A new `SimpleStore.Catalog.API` project was created

The commit adds a dedicated service project for Catalog:

- `src/SimpleStore.Catalog.API/Program.cs`
- `src/SimpleStore.Catalog.API/Endpoints/CatalogEndpoints.cs`
- `src/SimpleStore.Catalog.API/Services/CatalogService.cs`
- `src/SimpleStore.Catalog.API/Data/CatalogDbContext.cs`
- `src/SimpleStore.Catalog.API/Models/*`
- `src/SimpleStore.Catalog.API/Migrations/*`
- `src/SimpleStore.Catalog.API/CatalogSeeder.cs`

That tells us Catalog is no longer just a shared library. It now has:

- its own startup
- its own HTTP surface
- its own data access layer
- its own migration/seeding responsibility

### Why this was done

This change establishes **ownership**. Catalog should be the only part of the system that knows how catalog data is stored internally.

The service startup makes that explicit:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");
builder.Services.AddScoped<ICatalogService, CatalogService>();

var app = builder.Build();
app.MapCatalogEndpoints();
```

This is the core extraction step: the service now hosts the database access and publishes catalog functionality over HTTP.

### Catalog now owns its schema and seed data

Previously, catalog seeding lived in the shared `DbSeeder`. In v2 it moved into `CatalogSeeder` inside the service itself.

```csharp
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await CatalogSeeder.SeedAsync(context);
}
```

This matters because **data lifecycle should follow service ownership**. If Catalog owns the data, Catalog should also own:

- migrations
- schema changes
- startup seeding

That is a subtle but very important microservices habit.

---

## 2) Catalog data access moved from direct `DbContext` usage to HTTP API calls

Before v2, the Web project had its own catalog service that directly queried the database:

```csharp
public class CatalogService : ICatalogService
{
    private readonly CatalogDbContext _context;

    public async Task<IEnumerable<Product>> GetProductsAsync(int? categoryId = null, string? searchTerm = null)
    {
        var query = _context.Products.Include(p => p.Category).AsQueryable();
        ...
        return await query.ToListAsync();
    }
}
```

After v2, Web no longer uses `CatalogDbContext` for catalog operations. It depends on `ICatalogApiClient` instead:

```csharp
public class CatalogController : Controller
{
    private readonly ICatalogApiClient _catalog;

    public async Task<IActionResult> Index(int? categoryId, string? search, int page = 1)
    {
        var categories = await _catalog.GetCategoriesAsync(page: 1, pageSize: 100);
        var products = await _catalog.GetProductsAsync(
            page: page,
            pageSize: 12,
            categoryId: categoryId,
            search: search);
        return View(products);
    }
}
```

### Why this was done

This removes a dangerous coupling:

- **before**: Web knew catalog tables and EF entities
- **after**: Web only knows the API contract

That means Catalog can now change its internals without forcing Web/Admin to reference EF Core or catalog entities directly.

### A visible side effect: DTOs replace entities

The storefront views changed from domain/entity types to API contract types:

```csharp
@model PagedResult<ProductDto>
```

instead of:

```csharp
@model IEnumerable<SimpleStore.Data.Models.Product>
```

This is educationally important: in distributed systems, consumers usually should not depend on another service's persistence entities. They should depend on **transport models** instead.

---

## 3) A shared HTTP client pattern was introduced for Web and Admin

A new shared client library was added:

- `SimpleStore.Catalog.API.Client`

It contains:

- `ICatalogApiClient`
- `CatalogApiClient`
- `ProductDto`
- `CategoryDto`
- `PagedResult<T>`
- `AddCatalogApiClient(...)`

### The interface defines the contract the consumers use

```csharp
public interface ICatalogApiClient
{
    Task<PagedResult<ProductDto>> GetProductsAsync(...);
    Task<ProductDto?> GetProductByIdAsync(int id, ...);
    Task<int> GetProductCountAsync(...);

    Task<PagedResult<CategoryDto>> GetCategoriesAsync(...);
    Task<CategoryDto?> GetCategoryByIdAsync(int id, ...);

    Task<ProductDto> CreateProductAsync(ProductDto product, ...);
    Task UpdateProductAsync(int id, ProductDto product, ...);
    Task DeleteProductAsync(int id, ...);
}
```

### The implementation wraps `HttpClient`

```csharp
public class CatalogApiClient : ICatalogApiClient
{
    private readonly HttpClient _http;

    public async Task<ProductDto?> GetProductByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"api/catalog/products/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProductDto>(cancellationToken);
    }
}
```

### Aspire service discovery is built into registration

```csharp
public static IHttpClientBuilder AddCatalogApiClient(
    this IHostApplicationBuilder builder,
    string serviceName = "catalog")
{
    return builder.Services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(client =>
    {
        client.BaseAddress = new Uri($"https+http://{serviceName}");
    });
}
```

### Why this pattern matters

This is a common .NET microservice pattern:

- keep HTTP details in one place
- give callers a typed abstraction
- centralize serialization and status-code handling
- make service discovery/configuration reusable

It also helps later when you want to add:

- retries
- auth headers
- logging
- circuit breakers
- tracing

without rewriting every controller or Razor component.

### How Web and Admin changed

Both apps now register the typed client instead of using catalog EF access:

```csharp
builder.AddCatalogApiClient();
```

Admin pages show the difference clearly.

Before:

```csharp
@inject CatalogDbContext Db
```

After:

```csharp
@inject ICatalogApiClient Catalog
```

That one line captures the architectural shift: **the UI no longer talks to the database; it talks to the service**.

---

## 4) Aspire now orchestrates Catalog as its own service

The AppHost changed from wiring Web/Admin directly to `catalogdb` to wiring them to the `catalog` service.

### Before

```csharp
var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(catalogDb)
    ...;

var admin = builder.AddProject<Projects.SimpleStore_Admin>("admin")
    .WithReference(catalogDb)
    ...;
```

### After

```csharp
var catalog = builder.AddProject<Projects.SimpleStore_Catalog_API>("catalog")
    .WithReference(catalogDb)
    .WaitFor(catalogDb);

var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(catalog)
    .WaitFor(catalog);

var admin = builder.AddProject<Projects.SimpleStore_Admin>("admin")
    .WithReference(catalog)
    .WaitFor(catalog);
```

### Why this matters

Aspire is now modeling the real runtime dependency:

- Catalog service depends on `catalogdb`
- Web depends on Catalog service
- Admin depends on Catalog service

That is much more accurate than saying Web/Admin depend on the database directly.

This is important for learners because microservices architecture is not just code organization. It is also:

- runtime topology
- dependency direction
- startup sequencing
- service discovery

AppHost becomes an executable description of the architecture.

---

## 5) New catalog endpoints were added

The new service exposes minimal API endpoints under `/api/catalog`.

### Product endpoints

- `GET /api/catalog/products`
- `GET /api/catalog/products/count`
- `GET /api/catalog/products/{id}`
- `POST /api/catalog/products`
- `PUT /api/catalog/products/{id}`
- `DELETE /api/catalog/products/{id}`

### Category endpoints

- `GET /api/catalog/categories`
- `GET /api/catalog/categories/count`
- `GET /api/catalog/categories/{id}`
- `POST /api/catalog/categories`
- `PUT /api/catalog/categories/{id}`
- `DELETE /api/catalog/categories/{id}`

Example from the diff:

```csharp
products.MapGet("", async (
    ICatalogService service,
    int page = 1,
    int pageSize = 20,
    int? categoryId = null,
    string? search = null,
    CancellationToken ct = default) =>
{
    var result = await service.GetProductsAsync(page, pageSize, categoryId, search, ct);
    return Results.Ok(result);
});
```

### Why these endpoints matter

They turn catalog behavior into a reusable capability for any future consumer.

At v2, the consumers are:

- Web
- Admin

But later, the same API could also be called by:

- a mobile app
- an API gateway
- another microservice
- a background sync job

That is one of the biggest benefits of service extraction: once the capability is exposed cleanly, **more than one client can use it without sharing database access**.

### Interesting design details

The API adds server-side pagination and count endpoints through `PagedResult<T>`.

That is not just a UI improvement. It is a service design improvement because it prevents consumers from assuming they can always load the whole table in one in-process query.

In other words, the HTTP boundary encourages more scalable thinking.

---

## Architecture Diagram

## Before v2

```text
               +-------------------+
               |   catalogdb       |
               +-------------------+
                  ^            ^
                  |            |
                  | EF Core    | EF Core
                  |            |
        +----------------+   +----------------+
        | SimpleStore.Web|   | SimpleStore.Admin |
        +----------------+   +----------------+
```

**Characteristics:**

- Web and Admin both know catalog persistence details.
- Catalog logic is effectively embedded inside consuming apps.
- The database acts like the integration boundary.

## After v2

```text
               +-------------------+
               |   catalogdb       |
               +-------------------+
                         ^
                         | EF Core
                         |
             +---------------------------+
             | SimpleStore.Catalog.API   |
             | /api/catalog/...          |
             +---------------------------+
                    ^              ^
                    | HTTP         | HTTP
                    | via typed    | via typed
                    | client        | client
        +----------------+   +----------------+
        | SimpleStore.Web|   | SimpleStore.Admin |
        +----------------+   +----------------+
```

**Characteristics:**

- Catalog owns the database.
- Web and Admin depend on an API contract.
- Integration happens at the service boundary, not the table boundary.

---

## Key Takeaways

1. **Extract one capability at a time.**  
   v2 does not turn everything into services at once. It picks Catalog first. That is a realistic migration pattern.

2. **Move data ownership with the service.**  
   The `DbContext`, models, migrations, and seeding all moved into `SimpleStore.Catalog.API`.

3. **Replace shared persistence with shared contracts.**  
   `ProductDto`, `CategoryDto`, and `ICatalogApiClient` become the safe integration surface.

4. **Consumers should not query another service's database.**  
   Web/Admin now ask Catalog for data instead of joining directly on catalog tables.

5. **Typed clients make service-to-service calls easier to manage.**  
   They keep HTTP concerns out of UI logic and make the architecture easier to evolve.

6. **Runtime wiring matters too.**  
   Updating Aspire/AppHost is part of the extraction, because deployment topology must match code boundaries.

7. **An API boundary changes design behavior.**  
   Pagination, DTOs, and explicit endpoints appear because remote calls force more deliberate contracts.

---

## Trade-offs

## Benefits

### Clearer ownership

Catalog now clearly owns:

- catalog schema
- catalog business rules
- catalog CRUD/query behavior

That reduces ambiguity and prepares the system for independent change.

### Better separation of concerns

Web and Admin can focus on presentation and workflows instead of catalog persistence logic.

### Easier future evolution

This change creates the foundation for later additions such as:

- API gateway routing
- service authentication/authorization
- independent deployment
- caching at the API boundary
- resilience policies on HTTP clients

### Safer reuse

Any new consumer can use the Catalog API without needing direct database access.

## Costs / downsides

### More operational complexity

There is now another service to run, observe, configure, and debug.

### Network calls replace in-process calls

What used to be a local EF query is now an HTTP request, which introduces:

- latency
- failure modes
- serialization overhead
- dependency on service availability

### Extra contract maintenance

DTOs and typed clients must be maintained alongside the service implementation.

### Temporary duplication during migration

During service extraction, some models and logic may appear duplicated or reshaped because transport contracts are different from EF entities.

### Security is still evolving

The new Catalog service is internal-only in v2 and does not yet configure authentication. That is acceptable for an early internal extraction, but it is also a reminder that service extraction often happens in stages.

---

## Final Reflection

v2 is a great teaching example because it shows that microservices are **not** created by simply adding more projects. They are created by changing the **boundary of ownership**.

The most important architectural move in this commit is not just adding `SimpleStore.Catalog.API`. It is making every consumer stop doing this:

- referencing `CatalogDbContext`
- using catalog EF entities directly
- treating the database as a shared integration mechanism

and start doing this instead:

- calling a well-defined HTTP API
- using DTO contracts
- depending on a service, not a schema

That is the real lesson of v2.
