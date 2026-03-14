# VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry

OpenTelemetry package for VapeCache EF Core interceptor events.

## What this package provides

- Meter: `VapeCache.EFCore.Cache`
- ActivitySource: `VapeCache.EFCore.Cache`
- Observer implementation that maps `IEfCoreSecondLevelCacheObserver` events into OTEL metrics/activities
- DI registration extension that also enables EF observer callbacks

## Install

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore
dotnet add package VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry
```

## Register

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
