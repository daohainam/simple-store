using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Order.API;
using SimpleStore.Order.API.Consumers;
using SimpleStore.Order.API.Data;
using SimpleStore.Order.API.Endpoints;
using SimpleStore.Order.API.Services;
using SimpleStore.ServiceDefaults;

// Internal API on the Aspire network. Storefront endpoints require the caller's JWT (owner = sub claim);
// admin endpoints require the "Admin" role. Tokens are issued by SimpleStore.Identity.API.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// v9: EF Core retry-on-failure for transient Postgres errors. See Identity.API/Program.cs for rationale.
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb",
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

builder.Services.AddScoped<IOrderService, OrderService>();

// MassTransit + RabbitMQ. Order.API publishes OrderSubmittedEvent and consumes the saga's
// OrderConfirmedEvent / OrderCancelledEvent to flip Order.Status.
// EF Core outbox: IPublishEndpoint.Publish writes to OutboxMessage table inside the same
// DB transaction as the order; a hosted bus delivers from the outbox asynchronously.
// EF Core inbox makes the consumes exactly-once on retry.
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.AddConsumer<OrderConfirmedConsumer>();
    x.AddConsumer<OrderCancelledConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        // v9: explicit heartbeat keeps long-lived AMQP connections alive across NAT/LB idle timeouts;
        // automatic-recovery + topology-recovery are on by default in the RabbitMQ.Client driver.
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!), h =>
        {
            h.Heartbeat(TimeSpan.FromSeconds(30));
            h.RequestedConnectionTimeout(TimeSpan.FromSeconds(10));
        });

        // v9: in-process retry for transient consumer failures (DB blip, downstream timeout). After 5
        // attempts the message goes to the _error queue for operator replay. UseCircuitBreaker stops
        // hammering downstream when a sustained failure pattern is detected.
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

app.MapOrderEndpoints();

// Migrate on startup. The Order service owns orderdb's schema.
// v9: wrapped in StartupMigrationRunner — see Identity.API/Program.cs.
await StartupMigrationRunner.RunAsync(app, async (sp, _) =>
{
    var context = sp.GetRequiredService<OrderDbContext>();
    await OrderSeeder.SeedAsync(context);
});

app.Run();
