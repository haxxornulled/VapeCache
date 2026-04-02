# VapeCache.Extensions.AdminAuth

Reusable admin authentication wiring for VapeCache hosts.

## Install

```bash
dotnet add package VapeCache.Extensions.AdminAuth
```

## What it provides

- `AddVapeCacheAdminAuthentication(...)` service registration helper
- Optional JWT bearer bootstrap from `Authentication:JwtBearer`
- Admin policy registration (`RequireAuthenticatedUser` or dev override assertion)
- Startup validation that fails fast when admin authorization is required but no auth schemes exist

## Register

```csharp
using VapeCache.Extensions.AdminAuth;

builder.Services.AddVapeCacheAdminAuthentication(
    builder.Configuration,
    requireAdminAuthorization: true);
```

## Configuration keys

```json
{
  "Authentication": {
    "JwtBearer": {
      "Enabled": true,
      "Authority": "https://issuer.example.com/",
      "Audience": "api://vapecache-admin",
      "RequireHttpsMetadata": true,
      "SigningKey": "",
      "ValidIssuer": "",
      "ValidAudience": ""
    }
  }
}
```

Configure either `Authority` or `SigningKey` (not both).

## Docs

- Admin auth guide: https://github.com/haxxornulled/VapeCache/blob/main/docs/ADMIN_AUTH.md
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
- Aspire integration: https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPIRE_INTEGRATION.md
