Codex Result + VapeCache Quick Guide

Purpose
- Show a clean pattern for Result<T>/Option<T> and HybridCacheService usage.
- Keep async fast and predictable (ValueTask, zero-alloc serialization, early returns).

Result<T> patterns (LanguageExt)
- Prefer returning Result<T> from services/repositories.
- Handle Result at boundaries with early returns (no throw for flow control).
- Use LogFailure for unexpected failures; LogFailureMessage for expected failures.

Option<T> patterns
- Use Match/if to guard missing values; return early.
- Avoid throwing for missing data.

Async Result<ValueTask<T>>
- When you might have a fast sync path, return Result<ValueTask<T>>.
- Only await ValueTask after you confirm the Result succeeded.

Hybrid cache usage (ICacheService)
- Byte APIs:
  - GetAsync(key) -> byte[]?
  - SetAsync(key, ReadOnlyMemory<byte>, options)
  - RemoveAsync(key)
- Zero-alloc typed APIs:
  - GetAsync<T>(key, SpanDeserializer<T>, ct)
  - SetAsync<T>(key, value, serialize, options, ct)
  - GetOrSetAsync<T>(key, factory, serialize, deserialize, options, ct)

Breaker/failover controls
- IRedisCircuitBreakerState: read status (enabled/open/failures).
- IRedisFailoverController: ForceOpen/ClearForcedOpen/MarkRedisSuccess/Failure.

Autofac wiring
- Centralize registrations in CompositionRoot.BuildContainer(...)
- Register ICacheService -> HybridCacheService + stub IRedisCommandExecutor for demos.

Where to look
- Result helpers: ResultDemo/Application/Common/Extensions/ResultExtensions.cs
- DI wiring: ResultDemo/CompositionRoot.cs
- Cache examples: ResultDemo/Examples/HybridCacheServiceExamples.cs

Porting to Blazor
- Move CompositionRoot into a DI module or extension method.
- Replace stub Redis executor with real Redis connections.
- Keep cache examples as a service class and call from UI handlers.
