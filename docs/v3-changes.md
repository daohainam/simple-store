# v3 Changes: Extracting Identity into a Microservice and Adopting JWT Authentication

## Overview

Version 3 is the point where **identity stops being an in-process concern** of the web apps and becomes a **real microservice**.

Before v3, `SimpleStore.Web` and `SimpleStore.Admin` used ASP.NET Core Identity directly against `identitydb`. In v3, that responsibility moves into a new service: **`SimpleStore.Identity.API`**.

This version also introduces **JWT bearer authentication** across service boundaries:

- `SimpleStore.Identity.API` issues access tokens and refresh tokens.
- `SimpleStore.Web` and `SimpleStore.Admin` call Identity over HTTP instead of using `IdentityDbContext` directly.
- Other services, such as `SimpleStore.Catalog.API`, validate those JWTs to authorize requests.

In other words, v3 turns authentication from a local framework feature into a **distributed system capability**.

## Why This Matters

This change applies two important microservices principles.

### 1. Identity becomes its own service boundary

Authentication and user management are cross-cutting concerns. In a monolith, it is common to keep them in the same process as the UI. In a microservices architecture, that creates tight coupling:

- the UI owns user tables
- business services cannot independently validate callers
- every future service risks depending on the same shared database

By extracting identity into `SimpleStore.Identity.API`, SimpleStore moves toward **service ownership**:

- Identity owns `identitydb`
- Web and Admin become clients of Identity
- user registration, login, refresh, profile, and admin user management all go through an API contract

That is a core microservices idea: **services interact through APIs, not each other's databases or in-process objects**.

### 2. JWT enables authentication across service boundaries

Once identity is remote, cookie-based local sign-in is no longer enough. Other services need a portable proof of who the caller is.

JWT solves that by packaging identity claims into a signed token that any trusted service can validate, as long as they share:

- issuer
- audience
- signing key

That is why v3 adds shared JWT configuration in Aspire and uses it in Identity, Web, Admin, and Catalog.

A major architectural benefit is that **the authentication decision is centralized in Identity, but the validation step is decentralized**. Identity issues the token once; downstream services verify it themselves.

## What Changed

### 1. A new `SimpleStore.Identity.API` project

v3 adds a dedicated identity microservice with its own program startup, endpoints, services, data layer, migrations, and seeding.

At startup, the new service:

- connects to `identitydb`
- configures ASP.NET Core Identity
- configures JWT bearer authentication
- exposes minimal API endpoints under `/api/identity`
- migrates and seeds its own database

```csharp
builder.AddNpgsqlDbContext<IdentityDbContext>("identitydb");

builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<IIdentityService, IdentityService>();
```

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.MapIdentityEndpoints();
```

This is important because the service now owns the full identity lifecycle instead of letting Web or Admin do it locally.

#### New responsibilities in Identity.API

The new API exposes:

- anonymous endpoints: register, login, refresh, logout
- authenticated endpoints: `/me`, passkey management
- admin endpoints: user listing, count, update, lock, unlock

That means both customer identity flows and admin identity operations now have a single home.

### 2. Identity data moved out of `SimpleStore.Data`

Before v3, `IdentityDbContext` lived in `SimpleStore.Data`. The diff removes it from there and recreates it inside the new Identity service.

Old location removed:

```csharp
-public class IdentityDbContext : IdentityDbContext<ApplicationUser>
-{
-    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }
-}
```

New location added:

```csharp
public class IdentityDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
}
```

This is more than a file move. It changes **ownership**:

- `identitydb` belongs to Identity.API
- Web and Admin stop migrating and seeding identity data
- identity-related schema evolution now happens in the identity service itself

That reduces coupling and prevents UI applications from acting like mini-identity servers.

### 3. Authentication moved from local Identity sign-in to JWT tokens

Before v3, Web used `SignInManager` and `UserManager` directly. In v3, login and registration call Identity over HTTP.

For example, the Web login page changed from local password sign-in to an API call:

```csharp
var response = await _identity.LoginAsync(new LoginRequest
{
    Email = Input.Email,
    Password = Input.Password
});
```

Registration does the same:

```csharp
var response = await _identity.RegisterAsync(new RegisterRequest
{
    Email = Input.Email,
    FullName = Input.FullName,
    Password = Input.Password
});
```

This matters because the UI is no longer the source of truth for authentication. It becomes a **consumer** of the Identity service.

### 4. Token issuance and validation flow

#### Access token issuance

`JwtTokenService` creates a signed JWT using a shared issuer, audience, and symmetric signing key:

```csharp
var token = new JwtSecurityToken(
    issuer: _options.Issuer,
    audience: _options.Audience,
    claims: claims,
    notBefore: now,
    expires: expiresAt,
    signingCredentials: credentials);
```

Claims include:

- `sub` = user id
- `email`
- `name`
- `role`

That is why downstream services can authorize actions such as admin-only catalog writes.

#### Refresh token issuance

v3 also adds persistent refresh tokens in `identitydb`.

Key design choices from the diff:

- raw refresh tokens are generated randomly
- only a **SHA-256 hash** is stored in the database
- refresh tokens can be rotated
- old tokens are revoked and linked to replacements

```csharp
existing.RevokedAt = DateTime.UtcNow;
existing.ReplacedByTokenHash = newHash;
```

This is educationally important: long-lived sessions should not rely on long-lived JWTs alone. Short-lived access tokens plus revocable refresh tokens are a safer pattern.

#### Validation in other services

Services validate tokens with the same shared JWT settings. For example, Catalog now adds JWT bearer authentication:

```csharp
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
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });
```

That allows Catalog write endpoints to require an admin token:

```csharp
}).RequireAuthorization("Admin");
```

This is a classic microservices pattern: **Identity issues; other services verify**.

### 5. How Web authenticates against the Identity service

Web now authenticates users by calling `IIdentityApiClient` instead of using local Identity APIs.

Key changes:

- removed `IdentityDbContext` registration
- removed local ASP.NET Core Identity setup
- added `IIdentityApiClient`
- added JWT bearer authentication for inbound requests
- added a token store backed by `IDistributedCache`
- added `BearerTokenHandler` for outbound calls

A major design detail is the **BFF-style token storage**:

- browser gets only an opaque `ss_session` cookie
- access token and refresh token stay server-side in `IDistributedCache`

```csharp
public const string SessionCookieName = "ss_session";
```

This avoids putting JWTs directly in browser storage while still letting the app act on the user's behalf.

Web also uses `JwtBearerEvents.OnMessageReceived` to pull the token from the server-side store for each request, and refresh it automatically if needed.

That is a clever bridge between classic server-rendered UI and service-to-service JWT auth.

### 6. How Admin authenticates against the Identity service

Admin follows the same overall pattern as Web, but with one extra twist: it is a **Blazor Server** app.

Blazor Server does not always have a reliable `HttpContext` during later interactive operations, so v3 adds `CircuitTokenStore`:

```csharp
// HttpContext may be unavailable in interactive Blazor — fall back to cached.
return _hasCached ? _cached : null;
```

This is a good example of architecture being shaped by runtime behavior. The identity strategy is the same, but the storage adapter differs because Admin runs over a SignalR circuit.

Admin login also explicitly checks the returned roles and rejects non-admin users early:

```csharp
if (!response.User.Roles.Contains("Admin"))
{
    ErrorMessage = "Account does not have admin access.";
    return Page();
}
```

That improves clarity for learners: authentication answers **who are you?**; authorization answers **what are you allowed to do?**

### 7. Web and Admin now use Identity over HTTP for user features

This extraction is not limited to login.

The diff shows Web and Admin replacing direct Identity DB usage with API calls:

- Web profile page uses `GetMeAsync()` and `UpdateMeAsync()`
- Web passkey pages use Identity API endpoints
- Admin dashboard gets customer count via `GetUserCountAsync()`
- Admin customers page uses `GetUsersAsync()`, `UpdateUserAsync()`, `LockUserAsync()`, `UnlockUserAsync()`

Example from the Admin customers page:

```csharp
var users = await Identity.GetUsersAsync(page: 1, pageSize: 100);
```

This is exactly what you want in microservices learning material: the UI stops querying another service's tables and instead consumes a contract.

### 8. Changes to Aspire orchestration

AppHost is where the distributed system wiring becomes visible.

v3 adds:

- a new `identity` project resource
- shared JWT parameters: `jwt-key`, `jwt-issuer`, `jwt-audience`
- environment propagation of `Jwt__*` values to token issuers and validators
- service references from Web/Admin to Identity

```csharp
var jwtKey = builder.AddParameter("jwt-key", secret: true);
var jwtIssuer = builder.AddParameter("jwt-issuer");
var jwtAudience = builder.AddParameter("jwt-audience");
```

```csharp
var identity = builder.AddProject<Projects.SimpleStore_Identity_API>("identity")
    .WithReference(identityDb)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithEnvironment("Jwt__Issuer", jwtIssuer)
    .WithEnvironment("Jwt__Audience", jwtAudience);
```

```csharp
var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(catalog)
    .WithReference(identity)
    .WithReference(orderDb);
```

This is a great teaching moment: orchestration is not just about starting containers or projects. It also distributes **shared trust configuration** so independently deployed services can participate in the same authentication model.

## Architecture Diagram

```text
+-------------------+                         +------------------------------+
| Browser           |                         | SimpleStore.Identity.API     |
|                   | -- login/register ---> | /api/identity/*              |
| holds only        |                         | owns identitydb              |
| ss_session cookie | <-- JWT + refresh -----| issues JWT + refresh token   |
+---------+---------+                         +---------------+--------------+
          |                                                   |
          | ss_session cookie                                 | JWT signed with
          v                                                   | shared key/issuer/audience
+---------+-----------------------------------+               |
| SimpleStore.Web / SimpleStore.Admin         |               |
| - stores tokens server-side in cache        |               |
| - authenticates incoming requests with JWT  |               |
| - sends bearer tokens on outbound calls     |               |
+---------+-----------------------------------+               |
          |                                                   |
          | JWT in Authorization header    |
          v                                                   v
+------------------------------+            +--------------------------------+
| SimpleStore.Catalog.API      |            | Other future services          |
| validates JWT locally        |            | can validate same JWT locally  |
| admin writes require role    |            | without calling Identity each  |
+------------------------------+            +--------------------------------+
```

### Request flow summary

1. User logs in through Web or Admin.
2. Web/Admin call `Identity.API` over HTTP.
3. Identity verifies credentials and returns an access token + refresh token.
4. Web/Admin store those tokens server-side and give the browser only `ss_session`.
5. When Web/Admin call protected services, `BearerTokenHandler` attaches the JWT.
6. Services like Catalog validate the JWT locally and authorize based on claims such as `role=Admin`.
7. When the access token nears expiry, Web/Admin call Identity's refresh endpoint and rotate the refresh token.

## Key Takeaways

- **A microservice should own its own data and behavior.** Moving `IdentityDbContext` into `SimpleStore.Identity.API` enforces that.
- **UI apps should not reach into another bounded context's database.** Web and Admin now depend on an HTTP API instead.
- **JWT is useful because it separates token issuance from token validation.** Identity issues once; many services can verify.
- **Authorization claims travel with the token.** That is why Catalog can enforce admin-only writes without querying the identity database.
- **Refresh tokens matter in distributed systems.** They let you keep access tokens short-lived while still supporting smooth user sessions.
- **BFF-style token storage is a practical compromise.** The browser keeps only an opaque session cookie while the server keeps the real tokens.
- **Runtime model matters.** Admin needed `CircuitTokenStore` because Blazor Server behaves differently from MVC/Razor Pages.

## Trade-offs

### Pros

- **Clearer service boundaries**: identity is no longer mixed into the UI process.
- **Better reuse**: any current or future service can trust tokens from Identity.
- **Less database coupling**: Web and Admin no longer need direct identity DB access.
- **Stronger authorization model**: downstream services can enforce policies themselves.
- **Better scalability of architecture**: adding more services no longer requires copying local identity logic everywhere.

### Cons

- **More moving parts**: login is now a network call, not a local method call.
- **Operational complexity**: JWT key, issuer, and audience must stay consistent across services.
- **Refresh-token lifecycle adds complexity**: rotation, revocation, and storage must be handled correctly.
- **Debugging becomes more distributed**: auth issues may involve Web/Admin, Identity, AppHost config, and downstream service validation.
- **Immediate token invalidation is harder with JWT**: once an access token is issued, it usually remains valid until expiry unless extra revocation infrastructure is added.

## Final Thought

v3 is an excellent teaching version because it shows a common migration step in real systems:

> first extract a business API, then extract identity, then move authentication to signed tokens that every service can understand.

That is the moment where the application begins to behave like a genuine microservices system rather than a monolith with a few HTTP wrappers.
