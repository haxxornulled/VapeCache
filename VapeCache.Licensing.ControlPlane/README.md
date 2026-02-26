# VapeCache.Licensing.ControlPlane

Online revocation and kill-switch service for VapeCache Enterprise licenses.

## Features

- License-level revoke/activate
- Organization kill-switch on/off
- Signing-key-id revoke/activate
- Atomic JSON state persistence (survives restart)
- API-key protection with runtime options
- Autofac composition + Serilog logging

## Run

```powershell
dotnet run --project VapeCache.Licensing.ControlPlane -c Release
```

## Required Production Config

Set a strong API key and keep it out of source control:

```powershell
$env:RevocationControlPlane__RequireApiKey = "true"
$env:RevocationControlPlane__ApiKey = "<long-random-secret>"
$env:RevocationControlPlane__PersistencePath = "C:\\secure\\revocations-state.json"
```

## API

- `GET /api/v1/revocations/status/{licenseId}?organizationId=...&keyId=...`
- `POST /api/v1/revocations/licenses/{licenseId}/revoke`
- `POST /api/v1/revocations/licenses/{licenseId}/activate`
- `POST /api/v1/revocations/organizations/{organizationId}/kill-switch`
- `POST /api/v1/revocations/organizations/{organizationId}/restore`
- `POST /api/v1/revocations/keyids/{keyId}/revoke`
- `POST /api/v1/revocations/keyids/{keyId}/activate`
- `GET /api/v1/revocations/snapshot`

Send the API key via `X-VapeCache-ApiKey`.
