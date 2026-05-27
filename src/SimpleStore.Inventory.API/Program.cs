using KurrentDB.Client;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Inventory.API;
using SimpleStore.Inventory.API.Application.DeliveryNotes;
using SimpleStore.Inventory.API.Application.ReceiptNotes;
using SimpleStore.Inventory.API.Application.Reservations;
using SimpleStore.Inventory.API.Consumers;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Endpoints;
using SimpleStore.Inventory.API.EventStore;
using SimpleStore.Inventory.API.Projections;
using SimpleStore.Inventory.API.Projections.Checkpoints;

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

// --- Read side: Postgres ------------------------------------------------------
builder.AddNpgsqlDbContext<InventoryReadDbContext>("inventorydb");

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

// --- Application + projector --------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CreateDeliveryNoteHandler>();
builder.Services.AddScoped<CreateReceiptNoteHandler>();
builder.Services.AddScoped<CreateReservationHandler>();
builder.Services.AddScoped<InventoryProjector>();
builder.Services.AddScoped<CheckpointStore>();
builder.Services.AddHostedService<InventoryProjectionService>();

// --- MassTransit + RabbitMQ (v8) ----------------------------------------------
// Inventory joins the bus in v8. It consumes ReserveStockRequestedEvent (checkout saga) and
// publishes StockReservedEvent / StockReservationFailedEvent / StockLevelChangedEvent. The EF
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
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!));
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

builder.Services.AddOpenApi();

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
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<InventoryReadDbContext>();
    var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
    var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("InventorySeeder");
    await InventorySeeder.SeedAsync(context, eventStore, clock, logger);
}

app.Run();
