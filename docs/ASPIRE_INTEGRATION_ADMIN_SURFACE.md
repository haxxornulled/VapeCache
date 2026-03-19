# Aspire Integration for VapeCache Admin Surface

## Scope

This document defines how VapeCache appears in Aspire/AppHost and how operator links/commands connect to the VapeCache admin surface without coupling Aspire UI internals to VapeCache product UI.

## Resource Model in Aspire

- AppHost registers `vapecache-ui` as the product/admin resource.
- Redis is referenced via Aspire service discovery (`redis` connection resource or container resource).
- Aspire Dashboard remains a separate observability companion, not an embedded product shell.

## URL Surfacing

`VapeCache.AppHost` publishes URL annotations for:

- Product/admin pages (`/vapecache`, `/vapecache/stats`, `/vapecache/health`, `/vapecache/invalidation`, `/vapecache/autoscaler`, `/vapecache/spill`, `/vapecache/reconciliation`, `/vapecache/policies`, `/vapecache/streams`)
- Product workbench (`/cache-workbench`)
- Read-only APIs (`/vapecache/api/status`, `/vapecache/api/stats`, `/vapecache/api/dashboard/shared-snapshot`)

This keeps operator discovery centralized in Aspire resource details while UI rendering stays in VapeCache itself.

## Command Surface

AppHost commands target operational endpoints:

- `POST /vapecache/admin/breaker/force-open`
- `POST /vapecache/admin/breaker/clear`
- `GET /vapecache/admin/reconciliation/status`
- `POST /vapecache/admin/reconciliation/run`
- `POST /vapecache/admin/reconciliation/flush`
- `GET /vapecache/api/status`
- `GET /vapecache/api/stats`
- `GET /vapecache/api/dashboard/shared-snapshot`
- `GET /health`
- `GET /alive`

Commands remain small and operational. Rich workflows remain in the VapeCache admin UI.

## Local vs Remote Constraints

### Local Development

- Stream/intent endpoint gates can be open by default for developer productivity.
- Admin command rehearsal is expected in local AppHost runs before release flow.

### Remote/Shared Environments

- Admin control endpoints must be explicitly enabled and protected.
- Treat admin commands as privileged operations requiring auth and network isolation.
- Prefer read-only commands in broader environments; restrict mutating commands to trusted operators.

## What Stays in Aspire vs VapeCache UI

### Aspire Owns

- Resource topology and health at distributed app level.
- Cross-service logs/traces/metrics and command launch affordances.

### VapeCache UI Owns

- Product control workflows and cache-specific operational context.
- Policy and stream interpretation, operator guidance, and control-plane UX.
- Composition over runtime adapter contracts.

## Guardrails

- Do not embed Aspire dashboard components into VapeCache UI.
- Do not make Aspire dashboard the primary product UI.
- Keep admin controls on dedicated admin prefixes (`/vapecache/admin/*`), separated from read-only API diagnostics (`/vapecache/api/*`).
- Keep rollout additive and backward compatible with existing runtime service contracts.

