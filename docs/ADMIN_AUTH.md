# Admin Authentication (JWT)

The Blazor admin pages and admin control endpoints use the `VapeCacheAdmin` authorization policy.

For non-development environments, admin authorization is required. If no auth scheme is registered, startup fails fast.

Package: `VapeCache.Extensions.AdminAuth`

## Security Contract (Required)

If your app exposes VapeCache wrapper/admin endpoints, you must enforce all of the following:

- Keep breaker/control endpoints on an internal admin prefix (for example `/internal/vapecache-admin`).
- Require authentication and authorization for admin control endpoints.
- Do not expose breaker control routes on public wrapper prefixes like `/vapecache`.
- Keep `EnableIntentEndpoints` and `EnableLiveStream` disabled unless you explicitly need them.
- In non-development environments, fail startup if admin auth is required but no auth scheme is configured.

If you do not install `VapeCache.Extensions.AdminAuth`, you must implement equivalent controls yourself.

```csharp
using VapeCache.Extensions.AdminAuth;

builder.Services.AddVapeCacheAdminAuthentication(
    configuration: builder.Configuration,
    requireAdminAuthorization: true,
    authorizationPolicy: "VapeCacheAdmin",
    allowAnonymousAdminPolicy: false);
```

## BYO Auth (No AdminAuth Package)

If you choose a custom auth stack, wire equivalent protections:

```csharp
builder.Services
    .AddAuthentication()
    .AddJwtBearer();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("VapeCacheAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
    });
});

var app = builder.Build();

app.MapVapeCacheEndpoints(
    prefix: "/vapecache",
    includeBreakerControlEndpoints: false,
    includeLiveStreamEndpoint: false,
    includeIntentEndpoints: false,
    includeDashboardEndpoint: true);

app.MapVapeCacheAdminEndpoints(
    prefix: "/internal/vapecache-admin",
    requireAuthorization: true,
    authorizationPolicy: "VapeCacheAdmin");
```

Recommended: add your own startup validation to fail fast in production when no authentication scheme is configured.

## Azure AD / Entra ID (ready to paste)

Use `appsettings.Production.json`:

```json
{
  "Authentication": {
    "JwtBearer": {
      "Enabled": true,
      "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
      "Audience": "api://<your-app-id-uri>",
      "RequireHttpsMetadata": true
    }
  },
  "VapeCache": {
    "Endpoints": {
      "EnableBreakerControl": true,
      "EnableIntentEndpoints": false,
      "EnableLiveStream": false,
      "RequireAdminAuthorizationInDevelopment": true
    }
  }
}
```

## Auth0 (ready to paste)

Use `appsettings.Production.json`:

```json
{
  "Authentication": {
    "JwtBearer": {
      "Enabled": true,
      "Authority": "https://<tenant>.us.auth0.com/",
      "Audience": "https://vapecache-admin-api",
      "RequireHttpsMetadata": true
    }
  },
  "VapeCache": {
    "Endpoints": {
      "EnableBreakerControl": true,
      "EnableIntentEndpoints": false,
      "EnableLiveStream": false,
      "RequireAdminAuthorizationInDevelopment": true
    }
  }
}
```

## Optional symmetric key mode

For internal-only deployments without an external IdP:

```json
{
  "Authentication": {
    "JwtBearer": {
      "Enabled": true,
      "SigningKey": "<at-least-32-byte-secret>",
      "ValidIssuer": "vapecache-internal",
      "ValidAudience": "vapecache-admin",
      "RequireHttpsMetadata": true
    }
  }
}
```

## Notes

- Configure either `Authority` or `SigningKey` (not both).
- Keep `RequireHttpsMetadata=true` in production.
- In symmetric-key mode, set `Audience` or `ValidAudience`.
- Keep `EnableIntentEndpoints` and `EnableLiveStream` off unless needed.
- If you skip this package, implement the full security contract above in your own infrastructure layer.
