using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Payment.API;
using SimpleStore.Payment.API.Consumers;
using SimpleStore.Payment.API.Data;
using SimpleStore.Payment.API.Endpoints;
using SimpleStore.Payment.API.Services;
using SimpleStore.ServiceDefaults;

// SimpleStore.Payment.API — v12. A simple prepaid-balance payment service. Customers create an
// account (auto-provisioned) and deposit funds; the checkout saga charges the account for an order
// and the payment succeeds or fails based on the balance. User endpoints require the caller's JWT
// (owner = sub claim); admin endpoints require the "Admin" role. Owns paymentdb end-to-end.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// v11: URL-segment API versioning. See Identity.API/Program.cs for rationale.
builder.AddSimpleStoreApiVersioning();

// v9: EF Core retry-on-failure for transient Postgres errors. See Identity.API/Program.cs for rationale.
builder.AddNpgsqlDbContext<PaymentDbContext>("paymentdb",
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPaymentService, PaymentService>();

// MassTransit + RabbitMQ. Payment.API consumes ProcessPaymentRequestedEventV1 from the checkout
// saga and publishes PaymentSucceededEventV1 / PaymentFailedEventV1. The EF outbox lets the reply
// commit atomically with the balance change; the EF inbox makes the consume exactly-once on retry.
builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<PaymentDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.AddConsumer<ProcessPaymentRequestedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        // v9: explicit heartbeat keeps long-lived AMQP connections alive across NAT/LB idle timeouts.
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!), h =>
        {
            h.Heartbeat(TimeSpan.FromSeconds(30));
            h.RequestedConnectionTimeout(TimeSpan.FromSeconds(10));
        });

        // v9: in-process retry for transient consumer failures; circuit breaker for sustained ones.
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

app.MapPaymentEndpoints();

// Migrate on startup. The Payment service owns paymentdb's schema.
// v9: wrapped in StartupMigrationRunner — see Identity.API/Program.cs.
await StartupMigrationRunner.RunAsync(app, async (sp, _) =>
{
    var context = sp.GetRequiredService<PaymentDbContext>();
    await PaymentSeeder.SeedAsync(context);
});

app.Run();
