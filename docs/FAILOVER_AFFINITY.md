# Failover Affinity Hints

`VapeCacheFailoverAffinityOptions` controls sticky-session hints for ASP.NET Core apps when Redis failover is active.

Use this when:
- you run multiple app nodes behind a load balancer,
- local in-memory fallback is enabled,
- and you want clients to stay on the same node during Redis incidents.

## Why This Exists

When Redis is healthy, any node can serve requests.

When Redis is failing over and your app serves from local fallback state, sending the same client to different nodes can create inconsistent user experience (different cache warmth per node). Affinity hints reduce that risk.

## Enable In App

```csharp
builder.Services.AddVapeCacheFailoverAffinityHints(options =>
{
    options.NodeId = Environment.GetEnvironmentVariable("POD_NAME")
        ?? $"{Environment.MachineName}:{Environment.ProcessId}";
    options.CookieName = "VapeCacheAffinity";
    options.SetCookieOnlyWhenFailingOver = true;
});

app.UseVapeCacheFailoverAffinityHints();
```

## Option Reference

`VapeCacheFailoverAffinityOptions` source:
`VapeCache.Extensions.AspNetCore/VapeCacheFailoverAffinityOptions.cs`

- `Enabled` (default `true`): master switch for middleware behavior.
- `NodeId` (default `MachineName:ProcessId`): identity emitted in headers/cookies. Prefer stable per-node IDs (`POD_NAME`, VM hostname).
- `NodeHeaderName` (default `X-VapeCache-Node`): response header containing current node id.
- `StateHeaderName` (default `X-VapeCache-Failover-State`): response header containing failover state (`fallback-open` or `redis-healthy`).
- `CookieName` (default `VapeCacheAffinity`): cookie carrying node affinity hint.
- `CookieTtl` (default `00:20:00`): max age of affinity cookie.
- `SetCookieOnlyWhenFailingOver` (default `true`): if `true`, only emit cookie while failover is active.
- `EmitMismatchHeader` (default `true`): emits `X-VapeCache-Affinity-Mismatch=1` when incoming cookie points to a different node.

## Recommended Production Defaults

- Keep `SetCookieOnlyWhenFailingOver = true` unless your traffic manager requires persistent hinting.
- Keep `EmitMismatchHeader = true` for observability and incident triage.
- Set `NodeId` explicitly from orchestrator identity (Kubernetes pod name, ECS task id, etc.).
- Keep `CookieTtl` short enough to recover quickly after topology changes.

## Load Balancer Integration Notes

- This middleware **emits hints**. It does not force the load balancer to honor them.
- Configure your edge/LB to read the affinity cookie or node header if you need strict stickiness.
- If your LB already applies stickiness, keep names aligned and avoid conflicting cookies.

## Validation Rules

`AddVapeCacheFailoverAffinityHints` validates on startup:
- `NodeId`, `NodeHeaderName`, `StateHeaderName`, `CookieName` must be non-empty.
- `CookieTtl` must be greater than zero.

Invalid settings fail fast via options validation.

## Troubleshooting

- No headers/cookie:
  - ensure middleware is added with `app.UseVapeCacheFailoverAffinityHints()`.
  - ensure `Enabled=true`.
- No cookie during failover tests:
  - confirm circuit breaker is open.
  - or set `SetCookieOnlyWhenFailingOver=false` for always-on hinting.
- Unexpected node switching:
  - verify LB routing actually uses the emitted hint.
  - inspect `X-VapeCache-Affinity-Mismatch` and node/state headers.
