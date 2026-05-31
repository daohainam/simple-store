using KurrentDB.Client;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Inventory.API;
using SimpleStore.Inventory.API.Application.DeliveryNotes;
using SimpleStore.Inventory.API.Application.ReceiptNotes;
using SimpleStore.Inventory.API.Application.Reservations;
using SimpleStore.Inventory.API.Consumers;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Endpoints;
using SimpleStore.Inventory.API.EventStore;
using SimpleStore.Inventory.API.Infrastructure;
using SimpleStore.Inventory.API.Projections;
using SimpleStore.Inventory.API.Projections.Checkpoints;
using SimpleStore.ServiceDefaults;

// SimpleStore.Inventory.API — v7 bounded context.
//
// Write side: domain events appended to KurrentDB (one stream per aggregate).
// Read side : Postgres tables populated by an asynchronous projector that
//             subscribes to the event store. CQRS in its textbook form.
//
// Tokens are validated against the shared Jwt:* config; only callers carrying
// the "Admin" role hit any endpoint on this service in v7.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// v11: URL-segment API versioning. See Identity.API/Program.cs for rationale.
builder.AddSimpleStoreApiVersioning();

// --- Read side: Postgres ------------------------------------------------------
// v9: EF Core retry-on-failure for transient Postgres errors. See Identity.API/Program.cs for rationale.
builder.AddNpgsqlDbContext<InventoryReadDbContext>("inventorydb",
    configureSettings: settings =>
    {
        settings.DisableRetry = true;
        settings.CommandTimeout = 30;
    },
    configureDbContextOptions: opt =>
        opt.UseNpgsql(npgsql =>
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null)));

// --- Event store: KurrentDB ---------------------------------------------------
// The AppHost injects ConnectionStrings:kurrentdb. We register the SDK's
// KurrentDBClient as a singleton; everything else in the service touches it
// only through IEventStore, so this is the only KurrentDB-aware DI line.
builder.Services.AddSingleton<KurrentDBClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var connStr = cfg.GetConnectionString("kurrentdb")
        ?? throw new InvalidOperationException("Missing connection string 'kurrentdb'.");
    var settings = KurrentDBClientSettings.Create(connStr);
    return new KurrentDBClient(settings);
});
builder.Services.AddSingleton<EventTypeRegistry>();
builder.Services.AddSingleton<IEventStore, KurrentEventStore>();

// v9: readiness probe for KurrentDB so /health flips to 503 when the event store is unreachable.
// v10: tagged "ready" so it also shows up on /ready (the readiness-only endpoint). KurrentDB is
// a true dependency — if it's down the projector can't replay events and the read model goes
// stale, so the service should refuse traffic until it returns.
builder.Services.AddHealthChecks()
    .AddCheck<KurrentDbHealthCheck>("kurrentdb", tags: ["ready"]);

// --- Application + projector --------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CreateDeliveryNoteHandler>();
builder.Services.AddScoped<CreateReceiptNoteHandler>();
builder.Services.AddScoped<CreateReservationHandler>();
builder.Services.AddScoped<InventoryProjector>();
builder.Services.AddScoped<CheckpointStore>();
builder.Services.AddHostedService<InventoryProjectionService>();

// --- MassTransit + RabbitMQ (v8) ----------------------------------------------
// Inventory joins the bus in v8. It consumes ReserveStockRequestedEventV1 (checkout saga) and
// publishes StockReservedEventV1 / StockReservationFailedEventV1 / StockLevelChangedEventV1. The EF
// bus outbox lets the async projector publish integration events inside the same Postgres
// transaction as the read-model write (see InventoryProjectionService).
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<InventoryReadDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.AddConsumer<ReserveStockRequestedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        // v9: Rabbit heartbeat + MassTransit retry/CB. See Order.API/Program.cs for rationale.
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!), h =>
        {
            h.Heartbeat(TimeSpan.FromSeconds(30));
            h.RequestedConnectionTimeout(TimeSpan.FromSeconds(10));
        });

        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 5,
            minInterval: TimeSpan.FromSeconds(1),
            maxInterval: TimeSpan.FromSeconds(30),
            intervalDelta: TimeSpan.FromSeconds(2)));

        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        cfg.ConfigureEndpoints(ctx);
    });
});

// --- JWT bearer (mirrors Order.API) -------------------------------------------
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? string.Empty;
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? string.Empty;
var jwtKey = builder.Configuration["Jwt:Key"] ?? string.Empty;

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
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
});

// v11: explicit "v1" document name. See Identity.API/Program.cs.
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapInventoryEndpoints();

// Migrate on startup. The Inventory service owns inventorydb's schema. The projector
// (BackgroundService) starts after the WebApplication is built and will replay from
// FromAll.Start if projection_checkpoints is empty.
// v9: wrapped in StartupMigrationRunner — see Identity.API/Program.cs.
await StartupMigrationRunner.RunAsync(app, async (sp, _) =>
{
    var context = sp.GetRequiredService<InventoryReadDbContext>();
    var eventStore = sp.GetRequiredService<IEventStore>();
    var clock = sp.GetRequiredService<TimeProvider>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("InventorySeeder");
    await InventorySeeder.SeedAsync(context, eventStore, clock, logger);
});

app.Run();
