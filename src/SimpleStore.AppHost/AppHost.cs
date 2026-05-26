var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgWeb();

var catalogDb = postgres.AddDatabase("catalogdb");
var orderDb = postgres.AddDatabase("orderdb");
var identityDb = postgres.AddDatabase("identitydb");

// Cart.API stores its state in Redis; RedisInsight gives a dev-only UI.
var cartRedis = builder.AddRedis("cart-redis")
    .WithRedisInsight();

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
var catalog = builder.AddProject<Projects.SimpleStore_Catalog_API>("catalog")
    .WithReference(catalogDb)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(catalogDb);

// Order runs as its own microservice and is the only resource that talks to orderdb.
var order = builder.AddProject<Projects.SimpleStore_Order_API>("order")
    .WithReference(orderDb)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(orderDb);

// Cart runs as its own Redis-backed microservice. Validates JWTs but allows anonymous calls
// (anonymous carts identify via the X-Cart-Id header set by SimpleStore.Web).
var cart = builder.AddProject<Projects.SimpleStore_Cart_API>("cart")
    .WithReference(cartRedis)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience)
    .WaitFor(cartRedis);

// YARP-based API gateway. The single entry point Web/Admin use to reach any backend service.
// Routes /api/v1/<service>/* to the matching backend (path transform strips /v1/) and enforces
// per-route JWT authorization at the edge.
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
