using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SimpleStore.Identity.API;
using SimpleStore.Identity.API.Data;
using SimpleStore.Identity.API.Endpoints;
using SimpleStore.Identity.API.Models;
using SimpleStore.Identity.API.Services;
using SimpleStore.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// v9: EF Core retry-on-failure for transient Postgres errors (failover, restart, network hiccups).
// We disable Aspire's built-in simple retry (settings.DisableRetry = true) and configure our own
// exponential strategy via Npgsql — gives explicit control over retry count + max delay.
// CommandTimeout caps how long a single SQL statement may run before timing out.
builder.AddNpgsqlDbContext<IdentityDbContext>("identitydb",
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

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<IIdentityService, IdentityService>();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                string.IsNullOrEmpty(jwt.Key) ? new byte[32] : Convert.FromBase64String(jwt.Key)),
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

app.MapIdentityEndpoints();

// Migrate and seed on startup. The Identity service owns identitydb's schema.
// v9: wrapped in StartupMigrationRunner so a transient Postgres unreachability at boot retries
// with bounded exponential backoff instead of crash-looping the container.
await StartupMigrationRunner.RunAsync(app, async (sp, _) =>
{
    var context = sp.GetRequiredService<IdentityDbContext>();
    var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
    await IdentitySeeder.SeedAsync(context, userManager, roleManager);
});

app.Run();
