using System.Diagnostics;
using Microsoft.Extensions.Options;
using VapeCache.Extensions.EntityFrameworkCore;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry;

/// <summary>
/// Observer that emits OpenTelemetry metrics and activities for EF Core cache lifecycle events.
/// </summary>
public sealed class EfCoreOpenTelemetryObserver : IEfCoreSecondLevelCacheObserver
{
    private static readonly TimeSpan ActivityStopOffset = TimeSpan.FromMilliseconds(0.001);
    private readonly IOptionsMonitor<EfCoreOpenTelemetryOptions> _optionsMonitor;

    /// <summary>
    /// Creates an EF Core OpenTelemetry observer instance.
    /// </summary>
    public EfCoreOpenTelemetryObserver(IOptionsMonitor<EfCoreOpenTelemetryOptions> optionsMonitor)
    {
        _optionsMonitor = ParanoiaThrowGuard.Against.NotNull(optionsMonitor);
    }

    /// <inheritdoc />
    public void OnQueryCacheKeyBuilt(in EfCoreQueryCacheKeyBuiltEvent @event)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        var tags = CreateProviderTags(@event.ProviderName);
        EfCoreOpenTelemetryMetrics.QueryKeyBuilt.Add(1, tags);

        if (!options.EmitActivities)
            return;

        using var activity = StartActivity("efcore.cache.query_key_built", @event.CommandId, @event.ContextInstanceId, @event.ProviderName);
        activity?.SetTag("efcore.cache.parameter_count", @event.ParameterCount);
    }

    /// <inheritdoc />
    public void OnQueryExecutionCompleted(in EfCoreQueryExecutionCompletedEvent @event)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        var tags = CreateProviderTags(@event.ProviderName);
        tags.Add("succeeded", @event.Succeeded);
        EfCoreOpenTelemetryMetrics.QueryExecutionCompleted.Add(1, tags);
        EfCoreOpenTelemetryMetrics.QueryExecutionMs.Record(@event.DurationMs, tags);

        if (!@event.Succeeded)
            EfCoreOpenTelemetryMetrics.QueryExecutionFailed.Add(1, tags);

        if (!options.EmitActivities)
            return;

        var now = DateTime.UtcNow;
        using var activity = EfCoreOpenTelemetryMetrics.ActivitySource.StartActivity(
            "efcore.cache.query_execution_completed",
            ActivityKind.Internal,
            parentContext: default,
            tags: null,
            links: null,
            startTime: now.Subtract(TimeSpan.FromMilliseconds(Math.Max(0d, @event.DurationMs))));
        if (activity is null)
            return;

        activity.SetTag("db.system", "efcore");
        activity.SetTag("db.provider", @event.ProviderName);
        activity.SetTag("efcore.command_id", @event.CommandId);
        activity.SetTag("efcore.context_instance_id", @event.ContextInstanceId);
        activity.SetTag("efcore.cache.query_key", @event.CacheKey);
        activity.SetTag("efcore.cache.succeeded", @event.Succeeded);
        if (!string.IsNullOrWhiteSpace(@event.FailureType))
            activity.SetTag("error.type", @event.FailureType);
        if (!string.IsNullOrWhiteSpace(@event.FailureMessage))
            activity.SetTag("error.message", @event.FailureMessage);
        activity.SetEndTime(now.Add(ActivityStopOffset));
    }

    /// <inheritdoc />
    public void OnInvalidationPlanCaptured(in EfCoreInvalidationPlanCapturedEvent @event)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        var zoneCount = @event.Zones.Count;
        EfCoreOpenTelemetryMetrics.InvalidationPlanCaptured.Add(1);
        EfCoreOpenTelemetryMetrics.InvalidationPlanZoneCount.Record(zoneCount);

        if (!options.EmitActivities)
            return;

        using var activity = StartActivity(
            "efcore.cache.invalidation_plan_captured",
            commandId: Guid.Empty,
            @event.ContextInstanceId,
            providerName: null);
        activity?.SetTag("efcore.cache.invalidation.zone_count", zoneCount);
    }

    /// <inheritdoc />
    public void OnZoneInvalidated(in EfCoreZoneInvalidatedEvent @event)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        EfCoreOpenTelemetryMetrics.ZoneInvalidated.Add(1);

        if (!options.EmitActivities)
            return;

        using var activity = StartActivity(
            "efcore.cache.zone_invalidated",
            commandId: Guid.Empty,
            @event.ContextInstanceId,
            providerName: null);
        activity?.SetTag("efcore.cache.zone", @event.Zone);
        activity?.SetTag("efcore.cache.zone_version", @event.Version);
    }

    /// <inheritdoc />
    public void OnZoneInvalidationFailed(in EfCoreZoneInvalidationFailedEvent @event)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        EfCoreOpenTelemetryMetrics.ZoneInvalidationFailed.Add(1);

        if (!options.EmitActivities)
            return;

        using var activity = StartActivity(
            "efcore.cache.zone_invalidation_failed",
            commandId: Guid.Empty,
            @event.ContextInstanceId,
            providerName: null);
        activity?.SetTag("efcore.cache.zone", @event.Zone);
        activity?.SetTag("error.type", @event.FailureType);
        activity?.SetTag("error.message", @event.FailureMessage);
    }

    private static TagList CreateProviderTags(string providerName)
    {
        var tags = new TagList();
        tags.Add("provider", providerName);
        return tags;
    }

    private static Activity? StartActivity(string name, Guid commandId, Guid contextInstanceId, string? providerName)
    {
        if (!EfCoreOpenTelemetryMetrics.ActivitySource.HasListeners())
            return null;

        var activity = EfCoreOpenTelemetryMetrics.ActivitySource.StartActivity(name, ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("db.system", "efcore");
        if (!string.IsNullOrWhiteSpace(providerName))
            activity.SetTag("db.provider", providerName);
        if (commandId != Guid.Empty)
            activity.SetTag("efcore.command_id", commandId);
        activity.SetTag("efcore.context_instance_id", contextInstanceId);
        return activity;
    }
}
