# VapeCache.Extensions.AdminAuth

Reusable admin authentication wiring for VapeCache hosts.

## What it provides

- `AddVapeCacheAdminAuthentication(...)` service registration helper
- Optional JWT bearer bootstrap from `Authentication:JwtBearer`
- Admin policy registration (`RequireAuthenticatedUser` or dev override assertion)
- Startup validation that fails fast when admin authorization is required but no auth schemes exist

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
