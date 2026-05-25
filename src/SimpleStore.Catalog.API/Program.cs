using SimpleStore.Catalog.API;
using SimpleStore.Catalog.API.Data;
using SimpleStore.Catalog.API.Endpoints;
using SimpleStore.Catalog.API.Services;

// Internal-only service: runs on the Aspire network and is not directly reachable
// from end users. No authentication is configured here; revisit when externalizing.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");

builder.Services.AddScoped<ICatalogService, CatalogService>();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapCatalogEndpoints();

// Migrate and seed on startup. The Catalog service owns catalogdb's schema.
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await CatalogSeeder.SeedAsync(context);
}

app.Run();
