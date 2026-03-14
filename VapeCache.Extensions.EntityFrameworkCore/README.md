# VapeCache.Extensions.EntityFrameworkCore

EF Core adapter package for VapeCache second-level cache interception contracts.

## What this package provides

- deterministic EF query cache-key builder contract (`IEfCoreQueryCacheKeyBuilder`)
- default SHA-256 query key builder (`Sha256EfCoreQueryCacheKeyBuilder`)
- command interceptor hook for query-key generation (`VapeCacheEfCoreCommandInterceptor`)
- save-changes invalidation bridge interceptor (`VapeCacheEfCoreSaveChangesInterceptor`)
- DI registration and `DbContextOptionsBuilder` wiring helpers

## Install

```bash
dotnet add package VapeCache.Extensions.EntityFrameworkCore
```

## Register

```csharp
builder.Services.AddVapeCacheEntityFrameworkCore(options =>
{
    options.ZonePrefix = "ef";
    options.EnableObserverCallbacks = true;
});

builder.Services.AddDbContext<MyDbContext>((sp, db) =>
{
    db.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    db.UseVapeCacheEntityFrameworkCore(sp);
});
```

## Profiler Visibility Hook

Register an observer to receive interceptor events and correlate DB + cache activity.

Observer callbacks are disabled by default (`EnableObserverCallbacks = false`) to keep overhead at zero unless explicitly enabled.

```csharp
using System.Threading.Channels;

public enum EfProfilerEventType
{
    QueryKeyBuilt,
    QueryExecutionCompleted,
    InvalidationPlanCaptured,
    ZoneInvalidated,
    ZoneInvalidationFailed
}

public sealed record EfProfilerEvent(
    DateTimeOffset TimestampUtc,
    EfProfilerEventType EventType,
    Guid CommandId,
    Guid ContextInstanceId,
    string? ProviderName,
    string? CacheKey,
    double? DurationMs,
    bool? Succeeded,
    string? Zone,
    long? ZoneVersion,
    string? FailureType,
    string? FailureMessage,
    IReadOnlyList<string>? PlannedZones);

public sealed class EfCacheProfilerObserver : IEfCoreSecondLevelCacheObserver
{
    private readonly ChannelWriter<EfProfilerEvent> _writer;

    public EfCacheProfilerObserver(ChannelWriter<EfProfilerEvent> writer)
    {
        _writer = writer;
    }

    public void OnQueryCacheKeyBuilt(in EfCoreQueryCacheKeyBuiltEvent e)
    {
        _writer.TryWrite(new EfProfilerEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: EfProfilerEventType.QueryKeyBuilt,
            CommandId: e.CommandId,
            ContextInstanceId: e.ContextInstanceId,
            ProviderName: e.ProviderName,
            CacheKey: e.CacheKey,
            DurationMs: null,
            Succeeded: null,
            Zone: null,
            ZoneVersion: null,
            FailureType: null,
            FailureMessage: null,
            PlannedZones: null));
    }

    public void OnQueryExecutionCompleted(in EfCoreQueryExecutionCompletedEvent e)
    {
        _writer.TryWrite(new EfProfilerEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: EfProfilerEventType.QueryExecutionCompleted,
            CommandId: e.CommandId,
            ContextInstanceId: e.ContextInstanceId,
            ProviderName: e.ProviderName,
            CacheKey: e.CacheKey,
            DurationMs: e.DurationMs,
            Succeeded: e.Succeeded,
            Zone: null,
            ZoneVersion: null,
            FailureType: e.FailureType,
            FailureMessage: e.FailureMessage,
            PlannedZones: null));
    }

    public void OnInvalidationPlanCaptured(in EfCoreInvalidationPlanCapturedEvent e)
    {
        _writer.TryWrite(new EfProfilerEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: EfProfilerEventType.InvalidationPlanCaptured,
            CommandId: Guid.Empty,
            ContextInstanceId: e.ContextInstanceId,
            ProviderName: null,
            CacheKey: null,
            DurationMs: null,
            Succeeded: null,
            Zone: null,
            ZoneVersion: null,
            FailureType: null,
            FailureMessage: null,
            PlannedZones: e.Zones));
    }

    public void OnZoneInvalidated(in EfCoreZoneInvalidatedEvent e)
    {
        _writer.TryWrite(new EfProfilerEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: EfProfilerEventType.ZoneInvalidated,
            CommandId: Guid.Empty,
            ContextInstanceId: e.ContextInstanceId,
            ProviderName: null,
            CacheKey: null,
            DurationMs: null,
            Succeeded: true,
            Zone: e.Zone,
            ZoneVersion: e.Version,
            FailureType: null,
            FailureMessage: null,
            PlannedZones: null));
    }

    public void OnZoneInvalidationFailed(in EfCoreZoneInvalidationFailedEvent e)
    {
        _writer.TryWrite(new EfProfilerEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            EventType: EfProfilerEventType.ZoneInvalidationFailed,
            CommandId: Guid.Empty,
            ContextInstanceId: e.ContextInstanceId,
            ProviderName: null,
            CacheKey: null,
            DurationMs: null,
            Succeeded: false,
            Zone: e.Zone,
            ZoneVersion: null,
            FailureType: e.FailureType,
            FailureMessage: e.FailureMessage,
            PlannedZones: null));
    }
}

builder.Services.AddSingleton(Channel.CreateUnbounded<EfProfilerEvent>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<EfProfilerEvent>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<EfProfilerEvent>>().Reader);
builder.Services.AddSingleton<IEfCoreSecondLevelCacheObserver, EfCacheProfilerObserver>();
builder.Services.AddVapeCacheEntityFrameworkCore(options =>
{
    options.EnableObserverCallbacks = true;
});
```

Your profiler can consume `ChannelReader<EfProfilerEvent>` and correlate query and cache behavior using:

- `CommandId` for query-key + query-execution events
- `ContextInstanceId` for SaveChanges/invalidation events

## Notes

- This package is the EF adapter boundary. Core runtime packages remain EF-free.
- Query-key generation is deterministic and provider-aware.
- SaveChanges invalidation is zone-based and designed to integrate with existing tag/zone invalidation APIs.
