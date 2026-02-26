# VapeCache.Licensing

Enterprise licensing primitives for VapeCache packages.

## What This Package Does

- Validates `VC2` enterprise license tokens (`ES256` signature).
- Exposes licensing models used by enterprise packages.
- Enables feature-gated licensing checks (`persistence`, `reconciliation`).

## Audience

`VapeCache.Licensing` is primarily an internal dependency of:

- `VapeCache.Persistence`
- `VapeCache.Reconciliation`

`LicenseTokenIssuer` remains temporarily for compatibility but is deprecated and moving to a separate private license-authority service/repo.

## License Model

- Signature algorithm: `ES256`
- Token format: `VC2.{header}.{payload}.{signature}`
- Key identity (`kid`) and public-key verification support environment overrides, but overrides are disabled by default.
- Enable verifier override only when explicitly needed:
  - `VAPECACHE_LICENSE_ALLOW_VERIFIER_ENV_OVERRIDE=true`
- Runtime revocation checks can call the online control plane:
  - `VAPECACHE_LICENSE_REVOCATION_ENABLED=true`
  - `VAPECACHE_LICENSE_REVOCATION_ENDPOINT=...`
  - `VAPECACHE_LICENSE_REVOCATION_API_KEY=...`

## Documentation

- Project documentation: https://github.com/haxxornulled/VapeCache
- Enterprise information: https://vapecache.com
