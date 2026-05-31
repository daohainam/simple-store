using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Catalog.API;
using SimpleStore.Catalog.API.Consumers;
using SimpleStore.Catalog.API.Data;
using SimpleStore.Catalog.API.Endpoints;
using SimpleStore.Catalog.API.Services;
using SimpleStore.ServiceDefaults;

// Internal API on the Aspire network. Reads are anonymous (storefront browsing);
// writes require JWT-bearer auth with the "Admin" role — tokens are issued by SimpleStore.Identity.API.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// v11: URL-segment API versioning. See Identity.API/Program.cs for rationale.
builder.AddSimpleStoreApiVersioning();

// v9: EF Core retry-on-failure for transient Postgres errors. See Identity.API/Program.cs for rationale.
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb",
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

builder.Services.AddScoped<ICatalogService, CatalogService>();

// MassTransit + RabbitMQ. Catalog.API publishes ProductUpdatedEventV1 and, in v8, consumes
// StockLevelChangedEventV1 from Inventory.API to refresh the denormalized Product.Stock cache.
// (The v7 OrderSubmittedConsumer that decremented stock directly is gone — Inventory owns stock now.)
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<CatalogDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.AddConsumer<StockLevelChangedConsumer>();
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

// JWT bearer — same Jwt:Issuer/Audience/Key as Identity.API (propagated by AppHost via env).
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
            NameClaimType = "name",
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

app.MapCatalogEndpoints();

// Migrate and seed on startup. The Catalog service owns catalogdb's schema.
// v9: wrapped in StartupMigrationRunner — see Identity.API/Program.cs.
await StartupMigrationRunner.RunAsync(app, async (sp, _) =>
{
    var context = sp.GetRequiredService<CatalogDbContext>();
    await CatalogSeeder.SeedAsync(context);
});

app.Run();
