using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// SaveChanges interceptor that bridges changed EF entities to VapeCache zone invalidation.
/// </summary>
public sealed partial class VapeCacheEfCoreSaveChangesInterceptor : SaveChangesInterceptor
{
    private const int MaxFailureMessageLength = 512;
    private readonly IOptionsMonitor<EfCoreSecondLevelCacheOptions> _optionsMonitor;
    private readonly IVapeCache? _cache;
    private readonly IEfCoreSecondLevelCacheObserver[] _observers;
    private readonly ILogger<VapeCacheEfCoreSaveChangesInterceptor> _logger;
    private readonly ConcurrentDictionary<Guid, string[]> _pendingByContext = new();

    /// <summary>
    /// Creates an invalidation bridge interceptor instance.
    /// </summary>
    public VapeCacheEfCoreSaveChangesInterceptor(
        IOptionsMonitor<EfCoreSecondLevelCacheOptions> optionsMonitor,
        IEnumerable<IEfCoreSecondLevelCacheObserver> observers,
        ILogger<VapeCacheEfCoreSaveChangesInterceptor> logger,
        IVapeCache? cache = null)
    {
        _optionsMonitor = ParanoiaThrowGuard.Against.NotNull(optionsMonitor);
        _logger = ParanoiaThrowGuard.Against.NotNull(logger);
        ParanoiaThrowGuard.Against.NotNull(observers);
        _observers = observers as IEfCoreSecondLevelCacheObserver[] ?? observers.ToArray();
        _cache = cache;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CapturePendingZones(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CapturePendingZones(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ApplyPendingInvalidations(eventData.Context, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await ApplyPendingInvalidations(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
        => ClearPending(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearPending(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SaveChangesCanceled(DbContextEventData eventData)
        => ClearPending(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearPending(eventData.Context);
        return Task.CompletedTask;
    }

    private void CapturePendingZones(DbContext? context)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || !options.EnableSaveChangesInvalidation || context is null)
            return;

        var zones = CollectChangedEntityZones(context.ChangeTracker, options.ZonePrefix);
        if (zones.Length == 0)
        {
            _pendingByContext.TryRemove(context.ContextId.InstanceId, out _);
            return;
        }

        NotifyInvalidationPlanCaptured(
            context.ContextId.InstanceId,
            zones,
            options);
        _pendingByContext[context.ContextId.InstanceId] = zones;
    }

    private async ValueTask ApplyPendingInvalidations(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
            return;

        var contextId = context.ContextId.InstanceId;
        if (!_pendingByContext.TryRemove(contextId, out var zones) || zones.Length == 0)
            return;

        var options = _optionsMonitor.CurrentValue;

        if (_cache is null)
        {
            LogSkippedInvalidationCacheUnavailable(_logger, zones.Length);
            return;
        }

        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            try
            {
                var version = await _cache.InvalidateZoneAsync(zone, cancellationToken).ConfigureAwait(false);
                NotifyZoneInvalidated(
                    new EfCoreZoneInvalidatedEvent(
                        ContextInstanceId: contextId,
                        Zone: zone,
                        Version: version),
                    options);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogZoneInvalidationFailed(_logger, zone, ex);
                NotifyZoneInvalidationFailed(
                    new EfCoreZoneInvalidationFailedEvent(
                        ContextInstanceId: contextId,
                        Zone: zone,
                        FailureType: ex.GetType().FullName ?? ex.GetType().Name,
                        FailureMessage: TruncateFailureMessage(ex.Message)),
                    options);
            }
        }
    }

    private void ClearPending(DbContext? context)
    {
        if (context is null)
            return;

        _pendingByContext.TryRemove(context.ContextId.InstanceId, out _);
    }

    private static string[] CollectChangedEntityZones(ChangeTracker changeTracker, string zonePrefix)
    {
        if (string.IsNullOrWhiteSpace(zonePrefix))
            zonePrefix = "ef";

        var normalizedPrefix = zonePrefix.Trim();
        var entries = changeTracker.Entries();
        HashSet<string>? zones = null;

        foreach (var entry in entries)
        {
            if (!IsWriteState(entry.State))
                continue;

            var entityName = entry.Metadata.ClrType.Name;
            if (string.IsNullOrWhiteSpace(entityName))
                continue;

            zones ??= new HashSet<string>(StringComparer.Ordinal);
            zones.Add(string.Concat(normalizedPrefix, ":", entityName));
        }

        if (zones is null || zones.Count == 0)
            return Array.Empty<string>();

        var result = new string[zones.Count];
        zones.CopyTo(result);
        return result;
    }

    private static bool IsWriteState(EntityState state)
        => state is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private void NotifyInvalidationPlanCaptured(
        Guid contextInstanceId,
        IReadOnlyList<string> zones,
        EfCoreSecondLevelCacheOptions options)
    {
        if (!options.EnableObserverCallbacks || _observers.Length == 0)
            return;

        var payload = new EfCoreInvalidationPlanCapturedEvent(
            ContextInstanceId: contextInstanceId,
            Zones: zones);
        for (var i = 0; i < _observers.Length; i++)
        {
            try
            {
                _observers[i].OnInvalidationPlanCaptured(payload);
            }
            catch (Exception ex)
            {
                LogObserverCallbackFailed(_logger, nameof(IEfCoreSecondLevelCacheObserver.OnInvalidationPlanCaptured), ex);
            }
        }
    }

    private void NotifyZoneInvalidated(
        in EfCoreZoneInvalidatedEvent payload,
        EfCoreSecondLevelCacheOptions options)
    {
        if (!options.EnableObserverCallbacks || _observers.Length == 0)
            return;

        for (var i = 0; i < _observers.Length; i++)
        {
            try
            {
                _observers[i].OnZoneInvalidated(payload);
            }
            catch (Exception ex)
            {
                LogObserverCallbackFailed(_logger, nameof(IEfCoreSecondLevelCacheObserver.OnZoneInvalidated), ex);
            }
        }
    }

    private void NotifyZoneInvalidationFailed(
        in EfCoreZoneInvalidationFailedEvent payload,
        EfCoreSecondLevelCacheOptions options)
    {
        if (!options.EnableObserverCallbacks || _observers.Length == 0)
            return;

        for (var i = 0; i < _observers.Length; i++)
        {
            try
            {
                _observers[i].OnZoneInvalidationFailed(payload);
            }
            catch (Exception ex)
            {
                LogObserverCallbackFailed(_logger, nameof(IEfCoreSecondLevelCacheObserver.OnZoneInvalidationFailed), ex);
            }
        }
    }

    private static string TruncateFailureMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;
        if (message.Length <= MaxFailureMessageLength)
            return message;
        return message[..MaxFailureMessageLength];
    }

    [LoggerMessage(
        EventId = 15002,
        Level = LogLevel.Debug,
        Message = "Skipping EF save-changes cache invalidation because IVapeCache is unavailable. Zones={Zones}")]
    private static partial void LogSkippedInvalidationCacheUnavailable(ILogger logger, int zones);

    [LoggerMessage(
        EventId = 15003,
        Level = LogLevel.Warning,
        Message = "EF save-changes zone invalidation failed for zone {Zone}.")]
    private static partial void LogZoneInvalidationFailed(ILogger logger, string zone, Exception exception);

    [LoggerMessage(
        EventId = 15005,
        Level = LogLevel.Debug,
        Message = "EF observer callback failed. Callback={Callback}")]
    private static partial void LogObserverCallbackFailed(ILogger logger, string callback, Exception exception);
}
