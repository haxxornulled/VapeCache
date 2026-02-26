# License Control Plane

`VapeCache.Licensing.ControlPlane` is the online authority for enterprise license revocation and kill-switch decisions.

## Why This Exists

- Key compromise and account fraud need immediate deny, not next deploy.
- Enterprise features need centralized control without redeploying every app.
- Operations teams need one endpoint to query and mutate revocation state.

## Service Characteristics

- .NET 10 web service
- Autofac-first composition
- Serilog structured logging
- JSON state persisted with atomic writes
- API-key protected endpoints

## Endpoint Contract

`GET /api/v1/revocations/status/{licenseId}?organizationId=...&keyId=...`

Response shape (minimum fields consumed by runtime client):

```json
{
  "licenseId": "lic_123",
  "organizationId": "org_42",
  "keyId": "vc-main-2026",
  "revoked": false,
  "reason": "active",
  "source": "none",
  "updatedAtUtc": null
}
```

## Mutations

- Revoke/activate a single license id
- Enable/disable organization kill-switch
- Revoke/activate a signing key id

All mutation endpoints accept:

```json
{
  "reason": "incident-2026-02-26",
  "actor": "ops-oncall"
}
```

## Production Configuration

Use environment variables:

- `RevocationControlPlane__RequireApiKey=true`
- `RevocationControlPlane__ApiKey=<long-random-secret>`
- `RevocationControlPlane__ApiKeyHeaderName=X-VapeCache-ApiKey`
- `RevocationControlPlane__PersistencePath=C:\secure\revocations-state.json`

## Client Runtime Configuration

Configure enterprise apps:

- `VAPECACHE_LICENSE_REVOCATION_ENABLED=true`
- `VAPECACHE_LICENSE_REVOCATION_ENDPOINT=https://license-control-plane.internal`
- `VAPECACHE_LICENSE_REVOCATION_API_KEY=<same-secret>`
- `VAPECACHE_LICENSE_REVOCATION_FAIL_OPEN=true|false`
- `VAPECACHE_LICENSE_REVOCATION_TIMEOUT_MS=2000`
- `VAPECACHE_LICENSE_REVOCATION_CACHE_SECONDS=60`

## Operational Notes

- Keep this service separate from public OSS runtime hosts.
- Back up the state file and treat it as security-sensitive operational data.
- Rotate API keys and store them in secret management, not `appsettings.json`.
