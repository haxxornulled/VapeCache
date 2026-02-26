# License Operations Runbook

This runbook defines the operational process for VapeCache license signing and verification in production.

## Scope

- Enterprise signing/verification keys
- Customer license issuance lifecycle
- Emergency response for key compromise
- Rotation cadence and compatibility window

## Key Roles

1. `Signer`:
   - Owns private signing key material.
   - Generates customer license tokens.
2. `Verifier`:
   - Owns public verification key and active key id (`kid`) configuration.
   - Deploys validation key updates to runtime environments.
3. `Release Manager`:
   - Coordinates rollout windows and validates canary behavior.

## Baseline Controls

1. Keep private signing key in dedicated secret store (never source control).
2. Keep public verification key and `kid` in deploy-time config (env vars/secret manager).
3. Require dual control for signing-key replacement.
4. Keep issuance audit log for each generated key:
   - customer/org id
   - tier and feature entitlements
   - `kid`
   - issue time / expiry
   - signer identity

## Normal Rotation Procedure

1. Generate new keypair.
2. Assign new `kid` (example: `vc-main-2026-q2`).
3. Publish new public key + `kid` to staging verifiers.
4. Issue canary licenses using new `kid`.
5. Validate staging workloads for 24-48h.
6. Publish new verifier config to production.
7. Start issuing new customer licenses with new `kid`.
8. Keep previous public key accepted for a grace period (recommended: 30 days).
9. Retire old key after grace period and revoke old `kid`.

## Emergency Compromise Procedure

1. Freeze license issuance immediately.
2. Generate emergency replacement keypair.
3. Publish emergency verifier config to production (new `kid` only).
4. Reissue active customer licenses with new `kid`.
5. Invalidate compromised `kid` in runtime config.
6. Publish incident notice and customer action instructions.
7. Run post-incident review and update controls.

## Runtime Safety Expectations

1. Invalid/expired/untrusted licenses fail closed for enterprise-only extensions.
2. Core OSS-safe cache behavior remains functional when enterprise checks fail.
3. License validation failures are logged with reason code (no secret leakage).

## Minimum Monitoring

1. Count validation failures by reason:
   - invalid format
   - unknown `kid`
   - invalid signature
   - expired
   - missing feature entitlement
2. Alert on spikes in unknown `kid` and invalid signature failures.
3. Track enterprise feature activation attempts vs successful validations.

## Planned Hardening (Recommended)

1. Add revocation list / online check endpoint for hard deny of compromised licenses.
2. Add signed issuance ledger export for auditability.
3. Add canary validator endpoint in CI/CD smoke tests.
