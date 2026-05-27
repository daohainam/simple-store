using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Catalog.API;
using SimpleStore.Catalog.API.Consumers;
using SimpleStore.Catalog.API.Data;
using SimpleStore.Catalog.API.Endpoints;
using SimpleStore.Catalog.API.Services;

// Internal API on the Aspire network. Reads are anonymous (storefront browsing);
// writes require JWT-bearer auth with the "Admin" role — tokens are issued by SimpleStore.Identity.API.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");

builder.Services.AddScoped<ICatalogService, CatalogService>();

// MassTransit + RabbitMQ. Catalog.API publishes ProductUpdatedEvent and, in v8, consumes
// StockLevelChangedEvent from Inventory.API to refresh the denormalized Product.Stock cache.
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
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!));
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

builder.Services.AddOpenApi();

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
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await CatalogSeeder.SeedAsync(context);
}

app.Run();
