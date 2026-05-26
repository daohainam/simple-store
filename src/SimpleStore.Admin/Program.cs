using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Admin.Components;
using SimpleStore.Admin.Services.Auth;
using SimpleStore.Catalog.API.Client;
using SimpleStore.Identity.API.Client;
using SimpleStore.Order.API.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Razor Pages host the login/logout endpoints — Blazor components don't have a live HttpContext
// when handling form posts, so cookie write/clear has to happen in a classic Razor Page.
builder.Services.AddRazorPages();

// HTTP clients for each microservice. All three attach BearerTokenHandler so admin calls
// carry the user's JWT — Identity.API needs it for /users admin endpoints, Catalog.API
// needs it for write endpoints, Order.API needs it for the admin order endpoints.
builder.Services.AddTransient<BearerTokenHandler>();
builder.AddCatalogApiClient().AddHttpMessageHandler<BearerTokenHandler>();
builder.AddIdentityApiClient().AddHttpMessageHandler<BearerTokenHandler>();
builder.AddOrderApiClient().AddHttpMessageHandler<BearerTokenHandler>();

// JWT bearer — same Jwt:Issuer/Audience/Key as Identity.API. OnMessageReceived lifts
// the token out of the server-side cache via ss_session cookie.
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
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = async ctx =>
            {
                if (!string.IsNullOrEmpty(ctx.Token)) return;
                var store = ctx.HttpContext.RequestServices.GetRequiredService<ITokenStore>();
                var current = await store.GetAsync(ctx.HttpContext.RequestAborted);
                if (current is null) return;

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
                    catch { /* fall through */ }
                }
                ctx.Token = current.AccessToken;
            },
            OnChallenge = ctx =>
            {
                // Redirect browser navigations to login; leave API responses as 401.
                if (ctx.HttpContext.Request.Method == HttpMethods.Get &&
                    ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.HandleResponse();
                    var returnUrl = ctx.Request.Path + ctx.Request.QueryString;
                    ctx.Response.Redirect($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
    // Anyone authenticated reaching Admin pages must have role Admin (gate at the route level
    // via <AuthorizeRouteView>); the default policy enforces this when no explicit policy is set.
    options.FallbackPolicy = options.GetPolicy("Admin");
});

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<DistributedCacheTokenStore>();
builder.Services.AddScoped<ITokenStore, CircuitTokenStore>();

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

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
