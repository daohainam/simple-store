using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Catalog.API.Client;
using SimpleStore.Data;
using SimpleStore.Identity.API.Client;
using SimpleStore.Web.Services;
using SimpleStore.Web.Services.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Web no longer talks directly to identitydb — Identity.API owns it.
builder.AddNpgsqlDbContext<OrderDbContext>("orderdb");

// HTTP clients for both microservices. BearerTokenHandler stamps Authorization on outbound calls.
builder.Services.AddTransient<BearerTokenHandler>();
builder.AddCatalogApiClient().AddHttpMessageHandler<BearerTokenHandler>();
builder.AddIdentityApiClient();

// JWT bearer — tokens issued by Identity.API. OnMessageReceived lifts the JWT out of the
// server-side session cache (browser only holds an opaque ss_session cookie).
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
            // "name" is the FullName claim; User.Identity.Name shows a human-readable label.
            // User id is read via User.FindFirstValue("sub") in controllers/pages.
            NameClaimType = "name",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = async ctx =>
            {
                if (!string.IsNullOrEmpty(ctx.Token)) return;

                var store = ctx.HttpContext.RequestServices.GetRequiredService<ITokenStore>();
                var current = await store.GetAsync(ctx.HttpContext.RequestAborted);
                if (current is null) return;

                // Auto-refresh expired access tokens transparently for inbound auth.
                if (current.ExpiresAt <= DateTime.UtcNow.AddSeconds(30) && !string.IsNullOrEmpty(current.RefreshToken))
                {
                    var identity = ctx.HttpContext.RequestServices.GetRequiredService<IIdentityApiClient>();
                    try
                    {
                        var rotated = await identity.RefreshAsync(new RefreshRequest { RefreshToken = current.RefreshToken }, ctx.HttpContext.RequestAborted);
                        if (rotated is not null)
                        {
                            var next = new TokenSet
                            {
                                AccessToken = rotated.AccessToken,
                                RefreshToken = rotated.RefreshToken,
                                ExpiresAt = rotated.ExpiresAt
                            };
                            await store.SetAsync(next, ctx.HttpContext.RequestAborted);
                            ctx.Token = next.AccessToken;
                            return;
                        }
                    }
                    catch
                    {
                        // Fall through and present the (likely-expired) access token; validation will fail cleanly.
                    }
                }

                ctx.Token = current.AccessToken;
            }
        };
    });

builder.Services.AddAuthorization();

// Session-keyed cart + server-side JWT store both ride on the same IDistributedCache.
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenStore, DistributedCacheTokenStore>();

// Register services
builder.Services.AddScoped<ICartService, CartService>();
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

// Migrate Order database (Identity migrates itself in SimpleStore.Identity.API; Catalog likewise).
using (var scope = app.Services.CreateScope())
{
    var orderDb = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await orderDb.Database.MigrateAsync();
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Catalog}/{action=Index}/{id?}");

app.MapRazorPages();

app.Run();
