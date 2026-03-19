# VapeCache Admin UI Architecture

## Goals

- Provide a VapeCache product control-plane UI for runtime operations and diagnostics.
- Keep runtime behavior in runtime/services, and keep Razor focused on presentation.
- Preserve clean architecture boundaries while staying additive and migration-safe.
- Support enterprise-safe operational controls with secure-by-default deployment posture.

## Non-Goals

- Embedding Aspire Dashboard UI inside VapeCache product UI.
- Moving business/runtime logic into Blazor components.
- Rewriting core runtime or transport architecture for UI concerns.
- Duplicating control paths that bypass existing runtime contracts.

## Responsibility Split

### Aspire Dashboard

- Distributed app topology, logs, traces, metrics, and generic observability.
- AppHost resource URLs and resource command launch points.

### VapeCache Admin UI (`VapeCache.UI`)

- Product-specific control plane: stats, health, invalidation, autoscaler, spill, reconciliation, policies, streams.
- Operator-facing workflows backed by adapter contracts.

## Runtime / Service / UI Boundaries

### Runtime Layer

- Owns cache behavior, failover, reconciliation, autoscaling, spill, and policy registry semantics.
- Exposes runtime contracts via abstractions (`IVapeCache`, `ICacheStats`, `IRedisFailoverController`, etc.).

### UI Adapter Layer (`VapeCache.UI/Features/Admin`)

- `VapeCacheAdminOrchestrator` composes UI projections from contracts.
- Contract set for UI consumption:
  - `IVapeCacheAdminStatsSnapshotProvider`
  - `IVapeCacheAdminInvalidationOperationsFacade`
  - `IVapeCacheAdminAutoscalerStatusProvider`
  - `IVapeCacheAdminSpillDiagnosticsProvider`
  - `IVapeCacheAdminReconciliationStatusProvider`
  - `IVapeCacheAdminBreakerStatusProvider`
  - `IVapeCacheAdminPolicyInspectionProvider`
  - `IVapeCacheAdminEventStreamFeedProvider`
- Runtime-backed adapters implement these contracts and wrap existing runtime services.

### Razor Components

- Render projections and invoke orchestrator actions.
- No domain logic, no direct runtime service coupling.

## Package Ownership and Shape

- `VapeCache.Runtime` (current runtime modules): engine and operational behavior.
- `VapeCache.UI` (admin shell): product UI and orchestration adapters.
- `VapeCache.Extensions.Aspire`: Aspire-specific endpoint/resource integration.
- Separation remains additive and supports future rename/split (`VapeCache.Admin`) without runtime churn.

## Routing Model

Admin UI routes:

- `/vapecache`
- `/vapecache/stats`
- `/vapecache/health`
- `/vapecache/invalidation`
- `/vapecache/autoscaler`
- `/vapecache/spill`
- `/vapecache/reconciliation`
- `/vapecache/policies`
- `/vapecache/streams`

Read-only API diagnostics and optional live surfaces stay under `/vapecache/api/*`.
Admin control endpoints stay under `/vapecache/admin/*`.

## Security Considerations

- Admin UI and admin control endpoints require explicit auth policy (`VapeCacheAdmin`) when enabled.
- Control endpoints are isolated on dedicated admin prefix and are opt-in by configuration.
- Stream/intent endpoints are development-enabled by default and must be explicitly enabled for non-dev.
- Prefer read-only views by default; destructive operations require explicit operator intent.
- Treat admin UI and admin API as internal-only unless hardened with authN/authZ and network controls.

## Migration-Safe Rollout Plan

1. Add contracts and runtime adapters while preserving existing runtime services.
2. Route all admin pages through orchestrator + contracts; avoid direct runtime service calls in pages.
3. Add missing route shells (`/policies`, `/streams`) as additive pages.
4. Keep Aspire integration separate: resource links + commands, no UI embedding attempts.
5. Gate control endpoints by environment/config; validate with local rehearsal before remote rollout.
6. Expand controls incrementally only where existing runtime contracts already provide safe operations.

