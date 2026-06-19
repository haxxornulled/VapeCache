# VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry

OpenTelemetry package for VapeCache EF Core interceptor events.

## Use This Package When

- Meter: `VapeCache.EFCore.Cache`
- ActivitySource: `VapeCache.EFCore.Cache`
- Observer implementation that maps `IEfCoreSecondLevelCacheObserver` events into OTEL metrics/activities
- DI registration extension that also enables EF observer callbacks

## Install

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore
dotnet add package VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry
```

## Usage

```csharp
builder.Services.AddVapeCacheEntityFrameworkCore();
builder.Services.AddVapeCacheEfCoreOpenTelemetry();
```

## OpenTelemetry wiring

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("VapeCache.EFCore.Cache"))
    .WithTracing(t => t.AddSource("VapeCache.EFCore.Cache"));
```

## Notes

- `AddVapeCacheEfCoreOpenTelemetry()` auto-enables EF observer callbacks so telemetry events are emitted.
- Metrics use low-cardinality tags by default.
- Activity emission is optional (`EmitActivities`, enabled by default).

## Docs

- EF Core second-level cache guide: https://github.com/haxxornulled/VapeCache/blob/main/docs/EFCORE_SECOND_LEVEL_CACHE.md
- Aspire integration: https://github.com/haxxornulled/VapeCache/blob/main/docs/ASPIRE_INTEGRATION.md
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
