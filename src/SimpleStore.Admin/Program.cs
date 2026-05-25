using SimpleStore.Admin.Components;
using SimpleStore.Catalog.API.Client;
using SimpleStore.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Order and Identity still talk to Postgres directly.
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb");
builder.AddNpgsqlDbContext<IdentityDbContext>("identitydb");

// Catalog is a microservice — Admin reaches it over HTTP.
builder.AddCatalogApiClient();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
