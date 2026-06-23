var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgWeb();

var catalogDb = postgres.AddDatabase("catalogdb");
var orderDb = postgres.AddDatabase("orderdb");
var identityDb = postgres.AddDatabase("identitydb");
var inventoryDb = postgres.AddDatabase("inventorydb");
var checkoutDb = postgres.AddDatabase("checkoutdb");
var paymentDb = postgres.AddDatabase("paymentdb");

// Cart.API stores its state in Redis; RedisInsight gives a dev-only UI.
var cartRedis = builder.AddRedis("cart-redis")
    .WithRedisInsight();

// RabbitMQ is the event bus (MassTransit). Management plugin gives a dev-only web UI.
// v8 flows: Order.API publishes OrderSubmittedEventV1; Checkout.API (saga) consumes it and publishes
// ReserveStockRequestedEventV1; Inventory.API consumes that and publishes StockReserved /
// StockReservationFailed / StockLevelChanged; Checkout.API consumes the reserve results and
// publishes OrderConfirmed / OrderCancelled (Order.API consumes those); Catalog.API consumes
// StockLevelChanged to refresh its cached Product.Stock, and ProductUpdatedEventV1 → Cart.API.
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();

// KurrentDB (formerly EventStoreDB) is the event store for SimpleStore.Inventory.API.
// CommunityToolkit.Aspire.Hosting.KurrentDB v13.3.x ships an AddKurrentDB extension
// that wraps the kurrentplatform/kurrentdb container, exposes the admin/gRPC port,
// and injects a connection string named after the resource. Runs in insecure dev
// mode by default — production hardening (TLS, ACLs) is out of scope for v7.
var kurrentdb = builder.AddKurrentDB("kurrentdb")
    .WithDataVolume("kurrentdb-data");

// Shared JWT configuration: every service that issues OR validates tokens must agree on key + issuer + audience.
var jwtKey = builder.AddParameter("jwt-key", secret: true);
var jwtIssuer = builder.AddParameter("jwt-issuer");
var jwtAudience = builder.AddParameter("jwt-audience");

// Identity runs as its own microservice and is the only resource that talks to identitydb.
var identity = builder.AddProject<Projects.SimpleStore_Identity_API>("identity")
    .WithReference(identityDb)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(identityDb);

// Catalog runs as its own microservice and is the only resource that talks to catalogdb.
// Publishes ProductUpdatedEventV1 and consumes OrderSubmittedEventV1 — both ride the rabbitmq bus.
var catalog = builder.AddProject<Projects.SimpleStore_Catalog_API>("catalog")
    .WithReference(catalogDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(catalogDb)
    .WaitFor(rabbitmq);

// Order runs as its own microservice and is the only resource that talks to orderdb.
// Publishes OrderSubmittedEventV1 after checkout.
var order = builder.AddProject<Projects.SimpleStore_Order_API>("order")
    .WithReference(orderDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(orderDb)
    .WaitFor(rabbitmq);

// Cart runs as its own Redis-backed microservice. Validates JWTs but allows anonymous calls
// (anonymous carts identify via the X-Cart-Id header set by SimpleStore.Web).
// Consumes ProductUpdatedEventV1 to refresh denormalized cart line items.
var cart = builder.AddProject<Projects.SimpleStore_Cart_API>("cart")
    .WithReference(cartRedis)
    .WithReference(rabbitmq)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(cartRedis)
    .WaitFor(rabbitmq);

// Inventory runs as its own microservice with an event-sourced write side (KurrentDB)
// and a CQRS Postgres read side (inventorydb). v8 wires it onto RabbitMQ: it consumes
// ReserveStockRequestedEventV1 from the checkout saga and publishes StockReserved /
// StockReservationFailed / StockLevelChanged. It is now the single source of truth for stock.
var inventory = builder.AddProject<Projects.SimpleStore_Inventory_API>("inventory")
    .WithReference(inventoryDb)
    .WithReference(kurrentdb)
    .WithReference(rabbitmq)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(inventoryDb)
    .WaitFor(kurrentdb)
    .WaitFor(rabbitmq);

// Checkout runs the MassTransit saga that orchestrates the create-order → reserve-stock →
// process-payment → confirm/cancel flow. Pure orchestrator: no HTTP surface, no JWT. Owns
// checkoutdb (saga state) and rides RabbitMQ. Not referenced by the gateway (nothing calls it over HTTP).
var checkout = builder.AddProject<Projects.SimpleStore_Checkout_API>("checkout")
    .WithReference(checkoutDb)
    .WithReference(rabbitmq)
    .WaitFor(checkoutDb)
    .WaitFor(rabbitmq);

// Payment runs as its own microservice and is the only resource that talks to paymentdb (v12).
// It consumes ProcessPaymentRequestedEventV1 from the checkout saga and publishes
// PaymentSucceededEventV1 / PaymentFailedEventV1 based on the customer's account balance — the
// controllable gate that lets a demo drive a checkout to Confirmed or to Cancelled (+ stock release).
var payment = builder.AddProject<Projects.SimpleStore_Payment_API>("payment")
    .WithReference(paymentDb)
    .WithReference(rabbitmq)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(paymentDb)
    .WaitFor(rabbitmq);

// YARP-based API gateway. The single entry point Web/Admin use to reach any backend service.
// Routes /api/v1/<service>/* to the matching backend and enforces per-route JWT authorization at
// the edge. v11: backends serve /api/v{version}/... natively (Asp.Versioning.Http URL-segment
// versioning), so the gateway forwards the version segment through — no more path-strip transform.
var gateway = builder.AddProject<Projects.SimpleStore_Gateway>("gateway")
    .WithReference(identity)
    .WithReference(catalog)
    .WithReference(order)
    .WithReference(cart)
    .WithReference(inventory)
    .WithReference(payment)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(identity)
    .WaitFor(catalog)
    .WaitFor(order)
    .WaitFor(cart)
    .WaitFor(inventory)
    .WaitFor(payment);

var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(gateway)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(gateway);

// Admin has no cart UI; like Web, it only talks to the gateway.
var admin = builder.AddProject<Projects.SimpleStore_Admin>("admin")
    .WithReference(gateway)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(gateway);

builder.Build().Run();
