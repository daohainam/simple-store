using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Cart.API.Client;
using SimpleStore.Catalog.API.Client;
using SimpleStore.Identity.API.Client;
using SimpleStore.Order.API.Client;
using SimpleStore.Web.Services.Auth;
using SimpleStore.Web.Services.Cart;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// Cart-cookie infrastructure: anonymous browsers identify their cart with the ss_cart GUID;
// the CartIdHandler stamps it on every outbound Cart.API call.
builder.Services.AddScoped<CartCookieManager>();
builder.Services.AddTransient<CartIdHandler>();

// HTTP clients for each microservice. BearerTokenHandler stamps Authorization on outbound calls
// (no-op when the caller is anonymous); CartIdHandler adds X-Cart-Id for anonymous cart access.
builder.Services.AddTransient<BearerTokenHandler>();
builder.AddCatalogApiClient().AddHttpMessageHandler<BearerTokenHandler>();
builder.AddIdentityApiClient();
builder.AddOrderApiClient().AddHttpMessageHandler<BearerTokenHandler>();
builder.AddCartApiClient()
    .AddHttpMessageHandler<BearerTokenHandler>()
    .AddHttpMessageHandler<CartIdHandler>();

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

builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITokenStore, DistributedCacheTokenStore>();

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
app.UseAuthentication();
app.UseAuthorization();

// Runs after authentication so the merge sees the just-logged-in user.
app.UseMiddleware<CartMergeMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Catalog}/{action=Index}/{id?}");

app.MapRazorPages();

app.Run();
