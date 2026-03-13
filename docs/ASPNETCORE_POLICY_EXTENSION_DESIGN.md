# ASP.NET Core Policy Extension Design (1.x Safe)

Status: Implemented (1.x Additive)  
Owner: VapeCache OSS  
Last updated: 2026-03-12

## 1. TL;DR

We are not rewriting the engine.

We are adding a cleaner ASP.NET Core-facing policy layer on top of the current runtime and output-cache integration, while keeping:

- package IDs stable for 1.x
- existing registration methods working
- clean architecture boundaries intact
- current invalidation, telemetry, and Redis behavior untouched

This is an additive API evolution, not a destructive refactor.

## 2. Why This Doc Exists

Current state is already strong:

- `VapeCache.Extensions.AspNetCore` integrates with ASP.NET Core output caching
- `VapeCacheOutputCacheStore` is already in place
- tag invalidation already exists in runtime APIs
- Aspire endpoints already expose operational data (`/status`, `/stats`, `/stream`, `/dashboard`)

The gap is developer ergonomics for endpoint-level intent in MVC/minimal APIs.

## 3. Hard Constraints (Non-Negotiable)

From current repo policy:

- No package rename/split in 1.x (`docs/STABILITY_POLICY.md`, `docs/PACKAGE_COMPATIBILITY_PLAN.md`)
- Keep `VapeCache.Runtime` as canonical install package
- Preserve existing public APIs in minor/patch releases
- Keep current non-goals: no Lua/PubSub/Streams expansion in this effort (`docs/NON_GOALS.md`)

Implication:

We do not introduce a new package topology in this workstream.

## 4. Scope

### In Scope

- Add ASP.NET policy authoring model for endpoint-level caching intent
- Keep using ASP.NET Core output cache middleware/store model
- Map new policy metadata to existing VapeCache store + tag index + runtime semantics
- Maintain compatibility with existing:
  - `AddVapeCacheOutputCaching(...)`
  - `UseVapeCacheOutputCaching()`
  - `CacheWithVapeCache(...)`

### Out of Scope

- Replacing the core cache runtime
- Breaking existing configuration contracts
- Adding Redis command-surface features outside current cache scope
- Package split/rename in 1.x

## 5. Design Goals

1. Keep current code working exactly as-is.
2. Make endpoint policies more expressive than plain `CacheOutput(...)`.
3. Keep ASP.NET integration in `VapeCache.Extensions.AspNetCore` only.
4. Keep runtime policy enforcement in existing abstractions/runtime services.
5. Add clear migration path from current usage to richer policy usage.

## 6. Proposed Public Surface (Additive)

### 6.1 Service Registration

Keep existing:

```csharp
builder.Services.AddVapeCacheOutputCaching(...);
```

Add additive registration for policy metadata binding:

```csharp
builder.Services.AddVapeCacheAspNetPolicies(options =>
{
    options.AddPolicy("products", policy =>
    {
        policy.Ttl = TimeSpan.FromMinutes(5);
        policy.Tags("products", "catalog");
        policy.VaryByQuery();
        policy.WithIntent("QueryResult", "Products endpoint");
    });
});
```

### 6.2 Minimal API

Keep existing:

```csharp
app.MapGet("/products/{id}", handler).CacheWithVapeCache();
```

Add richer overloads:

```csharp
app.MapGet("/products/{id}", handler)
   .CacheWithVapeCache("products");

app.MapGet("/search", handler)
   .CacheWithVapeCache(policy =>
   {
       policy.Ttl(TimeSpan.FromSeconds(60));
       policy.VaryByQuery();
       policy.Tags("search");
   });
```

### 6.3 MVC / Controllers

Add attribute metadata support:

```csharp
[VapeCachePolicy("products")]
public async Task<IActionResult> GetProduct(int id) { ... }
```

Optional inline attribute mode:

```csharp
[VapeCachePolicy(TtlSeconds = 120, Tags = new[] { "catalog" }, VaryByQuery = true)]
public IActionResult Search(string q) { ... }
```

## 7. Policy Model (HTTP-Facing)

Add new ASP.NET-focused policy options type in `VapeCache.Extensions.AspNetCore`:

```csharp
public sealed class VapeCacheHttpPolicyOptions
{
    public TimeSpan? Ttl { get; set; }
    public bool VaryByQuery { get; set; }
    public string[] VaryByHeaders { get; set; } = Array.Empty<string>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? IntentKind { get; set; }
    public string? IntentReason { get; set; }
}
```

Important:

- This is ASP.NET-facing metadata, not a runtime rewrite.
- Mapping to runtime uses existing `CacheEntryOptions`, `CacheIntent`, and tag APIs.

## 8. Request Flow (No Engine Rewrite)

1. Endpoint metadata resolved (attribute or minimal API metadata).
2. Policy name/inline config resolved via ASP.NET extension layer.
3. Output-cache policy applied via existing middleware path.
4. `VapeCacheOutputCacheStore` persists payload.
5. Tag metadata (if present) uses existing tag indexing/version behavior.
6. Existing metrics/traces stay the same.

No change to core Redis transport orchestration required.

## 9. Clean Architecture Mapping

### `VapeCache.Extensions.AspNetCore`

- New metadata types
- Attribute definitions
- Endpoint extension methods
- Policy registry/binding for ASP.NET
- Mapping adapters to existing runtime abstractions

### `VapeCache.Abstractions` / `Infrastructure`

- No breaking contract changes
- No ASP.NET dependencies introduced
- Existing cache/invalidation/intent APIs reused

This preserves inward dependency flow from `docs/CLEAN_ARCHITECTURE.md`.

## 10. Backward Compatibility Contract

For 1.x:

- Existing methods stay valid and documented:
  - `AddVapeCacheOutputCaching(...)`
  - `UseVapeCacheOutputCaching()`
  - `CacheWithVapeCache(...)`
- Existing package IDs unchanged
- Existing settings continue to bind
- New policy APIs are additive only

## 11. Migration Plan

### Phase 1: Additive Scaffolding

- Introduce new policy options and extension points
- Keep current docs/examples as default path
- Add new "enhanced policy" examples beside them

### Phase 2: Policy Ergonomics

- Add MVC attribute metadata support
- Add minimal API inline policy builder overloads
- Add tests proving old/new APIs produce equivalent output-cache behavior

### Phase 3: Docs and Guidance

- Update README with one short policy snippet
- Add dedicated doc page for policy model
- Keep current quickstart intact; add optional "advanced policy" section

### Phase 4: Optional Deprecation (Future)

- Only after adoption data and successful release cycles
- Use `[Obsolete]` with migration guidance
- Never remove in same minor release

## 12. Test Plan

Add tests in `VapeCache.Tests/AspNetCore` for:

1. Existing API unchanged behavior.
2. Named policy mapping to output cache.
3. Attribute policy mapping on MVC actions.
4. Minimal API inline policy mapping.
5. Tag metadata path with `EnableTagIndexing=true`.
6. Failover-affinity middleware remains unaffected.

Regression guard:

- Existing AspNetCore tests must continue passing with no behavior drift.

## 13. Performance/Claims Guardrails

- No new benchmark claims without required report metadata (`docs/BENCHMARK_CLAIMS_POLICY.md`).
- Policy layer changes must not degrade hot-path behavior without explicit disclosure.

## 14. OSS vs Enterprise Boundary

OSS retains:

- Core runtime
- ASP.NET output-cache integration
- policy metadata model
- tag invalidation and telemetry baseline

Enterprise can layer:

- advanced policy packs
- org-specific policy governance
- admin UX and operational controls

No OSS/Enterprise fork in programming model is required.

## 15. Deliverables Checklist

- [x] Add `AddVapeCacheAspNetPolicies(...)` and options model
- [x] Add policy metadata attribute for MVC
- [x] Add minimal API inline policy overload
- [x] Add compatibility tests (old/new paths)
- [x] Add policy docs page with migration examples
- [x] Keep existing quickstart path valid and tested

Implementation references:

- `VapeCache.Extensions.AspNetCore/VapeCacheAspNetCoreCachingExtensions.cs`
- `VapeCache.Extensions.AspNetCore/VapeCacheHttpPolicyOptions.cs`
- `VapeCache.Extensions.AspNetCore/VapeCacheHttpPolicyBuilder.cs`
- `VapeCache.Extensions.AspNetCore/VapeCachePolicyAttribute.cs`
- `VapeCache.Extensions.AspNetCore/VapeCachePolicyMvcOptionsSetup.cs`
- `VapeCache.Tests/AspNetCore/VapeCacheAspNetPolicyErgonomicsTests.cs`
- `docs/ASPNETCORE_POLICY_EXTENSION.md`

## 16. Callout: What This Does Not Do

This plan does not:

- toast current runtime internals
- force package churn in 1.x
- pivot into full Redis-client scope
- invalidate existing consumer integrations

It tightens the front-door API while preserving the engine you already built.
