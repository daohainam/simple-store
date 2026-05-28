using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Cart.API.Consumers;
using SimpleStore.Cart.API.Endpoints;
using SimpleStore.Cart.API.Middleware;
using SimpleStore.Cart.API.Services;

// Internal API on the Aspire network. Cart data lives in Redis ("cart-redis" resource).
// Anonymous browsers identify with an X-Cart-Id header (GUID); authenticated callers
// are keyed by the JWT "sub" claim. The /merge endpoint folds the former into the latter.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddRedisDistributedCache("cart-redis");
// Also register the raw IConnectionMultiplexer (same Aspire resource) so RedisCartStore can SCAN
// every cart key — needed by ProductUpdatedConsumer to fan out denormalized refreshes.
builder.AddRedisClient("cart-redis");

builder.Services.AddScoped<ICartStore, RedisCartStore>();

// MassTransit + RabbitMQ. Cart.API only consumes — no DbContext, no outbox/inbox.
// Duplicate delivery is harmless: ProductUpdatedConsumer rewrites the same denormalized fields.
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ProductUpdatedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        // v9: Rabbit heartbeat + MassTransit retry/CB. See Order.API/Program.cs for rationale.
        // Cart has no DbContext/inbox so the consumer must remain idempotent (existing v6 design).
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

builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// v9: turn Redis transient failures on cart write paths into a clean 503 instead of a 500.
app.UseMiddleware<RedisExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapCartEndpoints();

app.Run();
