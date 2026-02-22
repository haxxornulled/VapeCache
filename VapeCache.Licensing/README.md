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

## License Model

- Signature algorithm: `ES256`
- Token format: `VC2.{header}.{payload}.{signature}`
- Key identity (`kid`) and public-key verification are configurable via environment variables.

## Documentation

- Project documentation: https://github.com/haxxornulled/VapeCache
- Enterprise information: https://vapecache.com
