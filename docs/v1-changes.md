# v1 Changes: Refactor to Database-per-Service with 3 DbContexts

## Overview

Version 1 is the first major architectural step away from a monolithic data model.

Before this change, SimpleStore stored **catalog, orders, and identity data in one EF Core `StoreDbContext`** backed by a single PostgreSQL database named `storedb`.

After this change, the application is reorganized around **three bounded contexts**:

- `CatalogDbContext` → `catalogdb`
- `OrderDbContext` → `orderdb`
- `IdentityDbContext` → `identitydb`

This is the classic **database-per-service** pattern: each domain gets its own schema boundary, migration history, and runtime connection. Even though `Web` and `Admin` still access these databases directly in v1, the data model is now split in a way that prepares the system for future service extraction.

---

## Why This Matters

In a monolith, a single database is convenient because every feature can join every table directly. The downside is that **all parts of the system become tightly coupled through the database**.

Microservices aim for the opposite: each service should **own its data** and evolve independently. The **database-per-service** pattern supports that by giving each business capability its own persistence boundary.

Why this matters in practice:

- **Catalog changes** should not accidentally break order storage.
- **Identity schema changes** should not force unrelated product/order migrations.
- Teams can reason about one domain at a time.
- Future extraction into separate APIs becomes much easier because the data is already partitioned.

In short, this refactor is less about “more databases” and more about **clear ownership boundaries**.

---

## What Changed

### 1. Old approach: one monolithic `StoreDbContext`

Previously, one EF Core context mixed together:

- ASP.NET Core Identity tables
- `Product` and `Category`
- `Order` and `OrderItem`

The removed context looked like this:

```csharp
public class StoreDbContext : IdentityDbContext<ApplicationUser>
{
    public StoreDbContext(DbContextOptions<StoreDbContext> options) : base(options) { }
    
    public DbSet<Product> Products { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }
}
```

This design was simple, but it created several problems:

- Catalog, orders, and identity were all coupled to one schema.
- A migration for one area affected the same database used by every other area.
- Cross-domain joins were easy, which encouraged tight coupling.
- The database became the integration point instead of the application boundary.

### 2. New approach: three focused DbContexts

The refactor replaces `StoreDbContext` with three separate contexts.

#### `CatalogDbContext`

```csharp
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; }
    public DbSet<Category> Categories { get; set; }
}
```

**Why:** Catalog data has its own lifecycle. Products and categories change for merchandising reasons, not for identity or order-processing reasons.

#### `OrderDbContext`

```csharp
public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }

    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }
}
```

**Why:** Orders are a separate business capability. Splitting them out allows order storage, querying, and later service extraction to evolve independently.

A subtle but important change appears in the order model: `OrderItem.ProductName` is stored directly in the order data.

```csharp
Items = cartItems.Select(i => new OrderItem
{
    ProductId = i.ProductId,
    ProductName = i.ProductName,
    Quantity = i.Quantity,
    UnitPrice = i.UnitPrice
}).ToList()
```

This is educationally important because it shows what happens when you stop relying on cross-database joins. Order views can no longer safely depend on joining back to catalog tables, so the order keeps a denormalized copy of product name.

#### `IdentityDbContext`

```csharp
public class IdentityDbContext : IdentityDbContext<ApplicationUser>
{
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }
}
```

**Why:** Authentication and user management are usually one of the first domains separated in microservice systems. Identity has different security, migration, and lifecycle concerns from business data.

### 3. Migrations were reorganized by bounded context

Before v1, there was effectively one migration stream under `SimpleStore.Data/Migrations` for the shared database.

After v1, migrations are split into separate folders:

- `Migrations/Catalog/`
- `Migrations/Order/`
- `Migrations/Identity/`

The CLI instructions were updated accordingly:

```pwsh
# Catalog
 dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context CatalogDbContext  --output-dir Migrations/Catalog

# Orders
 dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context OrderDbContext    --output-dir Migrations/Order

# Identity
 dotnet ef migrations add <Name> --project src/SimpleStore.Data --startup-project src/SimpleStore.Web --context IdentityDbContext --output-dir Migrations/Identity
```

**Why this matters:** once contexts are separated, each one needs its own model snapshot and migration history. That prevents a catalog table change from showing up in the identity migration chain.

This also makes ownership visible in the repository: developers can now see which migration belongs to which domain.

### 4. Separate design-time factories for EF tooling

The old shared factory was removed and replaced with one factory per context, for example:

```csharp
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__catalogdb")
    ?? "Host=localhost;Database=catalogdb;Username=postgres;******";
```

Equivalent factories were added for `orderdb` and `identitydb`.

**Why:** `dotnet ef` needs a clear way to construct each context independently. Once there are multiple databases, a single generic design-time factory is no longer enough.

### 5. AppHost orchestration now exposes three database resources

Before:

```csharp
var storeDb = postgres.AddDatabase("storedb");
```

After:

```csharp
var catalogDb = postgres.AddDatabase("catalogdb");
var orderDb = postgres.AddDatabase("orderdb");
var identityDb = postgres.AddDatabase("identitydb");
```

And both apps now receive references to all three:

```csharp
var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(catalogDb)
    .WithReference(orderDb)
    .WithReference(identityDb)
    .WaitFor(catalogDb)
    .WaitFor(orderDb)
    .WaitFor(identityDb);
```

**Why:** in Aspire, resource references are how connection information flows into projects. Splitting the logical databases in AppHost makes the new architecture explicit at the orchestration layer, not just in EF Core.

### 6. Web and Admin now connect to specific databases instead of one shared context

#### Web

`SimpleStore.Web` changed from:

```csharp
builder.AddNpgsqlDbContext<StoreDbContext>("storedb");
```

to:

```csharp
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb");
builder.AddNpgsqlDbContext<IdentityDbContext>("identitydb");
```

Identity was also re-pointed to the new auth database:

```csharp
.AddEntityFrameworkStores<IdentityDbContext>()
```

Startup migration/seeding was split as well:

```csharp
await identityDb.Database.MigrateAsync();
await orderDb.Database.MigrateAsync();
await DbSeeder.SeedCatalogAsync(catalogDb);
await DbSeeder.SeedIdentityAsync(userManager);
```

**Why:** Web now resolves the correct persistence boundary for each feature instead of treating all data as one big store.

#### Admin

`SimpleStore.Admin` now registers the same three contexts:

```csharp
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb");
builder.AddNpgsqlDbContext<IdentityDbContext>("identitydb");
```

Pages were updated to inject only the contexts they need.

Examples:

- `Categories.razor` and `Products.razor` now use `CatalogDbContext`
- `Customers.razor` uses `IdentityDbContext` plus `OrderDbContext`
- `Orders.razor` uses `OrderDbContext` plus `IdentityDbContext`
- `Home.razor` pulls summary numbers from all three contexts

A good example is `Customers.razor`:

```csharp
@inject IdentityDbContext IdentityDb
@inject OrderDbContext OrderDb

var orderCounts = await OrderDb.Orders
    .GroupBy(o => o.UserId)
    .ToDictionaryAsync(x => x.UserId, x => x.Count);

var users = await IdentityDb.Users
    .OrderBy(u => u.Email)
    .ToListAsync();
```

**Why:** this makes cross-context access explicit. Instead of one hidden all-powerful context, the code now shows exactly which domain data a page depends on.

### 7. Cross-database joins were replaced by application-level composition

The diff documentation explicitly notes:

> There are no cross-database foreign keys. `Order.UserId` is a soft reference to `AspNetUsers.Id` in `identitydb`; `OrderItem.ProductId` is a soft reference to `Products.Id` in `catalogdb`. Joins across DBs are done in application code.

This is one of the biggest conceptual changes in the whole refactor.

**Old mindset:** “I can join anything in SQL because it is all in one database.”

**New mindset:** “Each domain owns its own data, so cross-domain views must be assembled by the application.”

That is much closer to real microservices architecture.

---

## Architecture Diagram

### Before: single shared database

```text
                 +----------------------+
                 |  SimpleStore.Web     |
                 +----------+-----------+
                            |
                            |
                 +----------v-----------+
                 |    StoreDbContext    |
                 +----------+-----------+
                            |
                            |
                 +----------v-----------+
                 |       storedb        |
                 |----------------------|
                 | Identity tables      |
                 | Categories           |
                 | Products             |
                 | Orders               |
                 | OrderItems           |
                 +----------+-----------+
                            ^
                            |
                 +----------+-----------+
                 |  SimpleStore.Admin   |
                 +----------------------+
```

### After: database-per-service split

```text
        +----------------------+          +----------------------+
        |  SimpleStore.Web     |          |  SimpleStore.Admin   |
        +----------+-----------+          +----------+-----------+
                   |                                 |
                   | registers 3 contexts            | registers 3 contexts
                   |                                 |
     +-------------+-------------+     +-------------+-------------+
     |             |             |     |             |             |
+----v----+   +----v----+   +----v----+              |             |
| Catalog |   | Order   |   | Identity|              |             |
|DbContext|   |DbContext|   |DbContext|              |             |
+----+----+   +----+----+   +----+----+              |             |
     |             |             |                   |             |
     +-------------+-------------+-------------------+-------------+
                                   Aspire references
                                             |
                          +------------------v------------------+
                          |         PostgreSQL resource          |
                          |--------------------------------------|
                          | catalogdb  | orderdb | identitydb    |
                          +--------------------------------------+
```

> Important learning note: in a fully isolated microservices system, each service would normally be the only runtime that talks to its own database. v1 is a **transitional architecture**: the data is partitioned first, and stricter service boundaries can come later.

---

## Key Takeaways

1. **Database-per-service starts with ownership, not deployment.**  
   You do not need separate executables on day one. Splitting the data model into bounded contexts is already meaningful progress.

2. **A shared database hides coupling.**  
   `StoreDbContext` made it easy to mix unrelated concerns. The new contexts force the code to reveal its dependencies.

3. **Separate migrations are a big deal.**  
   Migration history is part of service ownership. Independent contexts need independent migration streams.

4. **Cross-domain reads become explicit.**  
   Instead of SQL joins across unrelated tables, the app now composes data in memory using soft references.

5. **Denormalization becomes useful.**  
   Storing `OrderItem.ProductName` is a practical response to the loss of convenient cross-database joins.

6. **This refactor prepares future extraction.**  
   Once catalog, orders, and identity already have separate databases and models, moving them into separate APIs becomes much easier.

---

## Trade-offs

### Pros

- **Clearer domain boundaries** between catalog, ordering, and identity
- **Safer schema evolution** because each area has its own migrations
- **Better alignment with microservices principles**
- **Easier future extraction** into standalone services
- **Reduced accidental coupling** through one giant shared context

### Cons

- **More operational complexity**: three contexts, three connection strings, three migration streams
- **No simple cross-database joins**: reporting and dashboards must compose data in code
- **Potential duplication**: fields like `ProductName` may need to be copied into other domains
- **This version is still transitional**: `Web` and `Admin` both touch all three databases directly, so runtime ownership is not fully isolated yet

The main lesson is that database-per-service is not free. You trade convenience for autonomy. In microservices, that trade is often worth it because independent evolution matters more than short-term simplicity.

---

## Final Thought

v1 is a strong teaching example because it shows a realistic first step in a migration:

- not a full microservices rewrite,
- not yet strict service isolation,
- but a deliberate restructuring of data boundaries.

That is often how successful microservice migrations begin: **first separate ownership, then separate runtime responsibilities.**
