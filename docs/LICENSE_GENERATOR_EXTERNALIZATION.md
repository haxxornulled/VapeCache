# License Generator Externalization Plan

This plan separates private license issuance from the main product repo.

## Goal

Keep verification code in product runtime, but move signing/generation workflows into a private authority repo.

## Current State

- Runtime verification in `VapeCache.Licensing`
- CLI generator in `VapeCache.LicenseGenerator`
- Revocation/kill-switch in `VapeCache.Licensing.ControlPlane`

## Target State

- Public/enterprise runtime repos keep only verification + feature gating.
- Private licensing repo/service owns:
  - private signing keys
  - issuance policies
  - customer entitlement issuance
  - audit ledger

## Migration Steps

1. Create private repo (example: `VapeCache-LicenseAuthority`).
2. Move `VapeCache.LicenseGenerator` there.
3. Keep `VapeCache.Licensing` in this repo with verification-only public key path.
4. Replace any local issuer automation with signed requests to private authority service.
5. Rotate signing key id and publish updated public key to verifier config.
6. Remove generator project from this solution when private service is validated.

## Safety Rules

- Never commit signing private keys.
- Keep verifier env override disabled by default.
- Treat revocation control plane as independent kill-switch authority.
