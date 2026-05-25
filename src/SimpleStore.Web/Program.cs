using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Data;
using SimpleStore.Data.Identity;
using SimpleStore.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Add DbContexts (one per bounded context, each with its own database)
builder.AddNpgsqlDbContext<CatalogDbContext>("catalogdb");
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb");
builder.AddNpgsqlDbContext<IdentityDbContext>("identitydb");

// Add Identity (schema v3 enables the passkey table)
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<IdentityDbContext>()
.AddDefaultTokenProviders();

builder.Services.Configure<IdentityPasskeyOptions>(options =>
{
    options.AuthenticatorTimeout = TimeSpan.FromMinutes(2);
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.LogoutPath = "/Identity/Account/Logout";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.SlidingExpiration = true;
});

// Session for cart
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpContextAccessor();

// Register services
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<IOrderService, OrderService>();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

// Seed databases
using (var scope = app.Services.CreateScope())
{
    var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var orderDb = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    var identityDb = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    await identityDb.Database.MigrateAsync();
    await orderDb.Database.MigrateAsync();
    await DbSeeder.SeedCatalogAsync(catalogDb);
    await DbSeeder.SeedIdentityAsync(userManager);
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Catalog}/{action=Index}/{id?}");

app.MapRazorPages();

app.Run();
