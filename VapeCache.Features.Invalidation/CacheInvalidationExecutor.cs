using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Features.Invalidation;

/// <summary>
/// Executes invalidation plans against <see cref="IVapeCache"/>.
/// </summary>
public sealed partial class CacheInvalidationExecutor(
    IVapeCache cache,
    IOptionsMonitor<CacheInvalidationOptions> optionsMonitor,
    ILogger<CacheInvalidationExecutor> logger) : ICacheInvalidationExecutor
{
    private readonly IVapeCache _cache = cache;
    private readonly IOptionsMonitor<CacheInvalidationOptions> _optionsMonitor = optionsMonitor;
    private readonly ILogger<CacheInvalidationExecutor> _logger = logger;

    public async ValueTask<CacheInvalidationExecutionResult> InvalidateAsync(
        CacheInvalidationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.TotalTargets == 0)
            return default;

        var options = _optionsMonitor.CurrentValue;
        var runtime = options.ResolveRuntimeSettings();
        if (!options.Enabled)
        {
            LogInvalidationDisabled(_logger, plan.TotalTargets);
            return new CacheInvalidationExecutionResult(
                RequestedTargets: plan.TotalTargets,
                InvalidatedTargets: 0,
                FailedTargets: 0,
                SkippedTargets: plan.TotalTargets,
                PolicyFailures: 0);
        }

        var invalidated = 0;
        var failed = 0;
        var skipped = 0;

        if (options.EnableTagInvalidation)
            await InvalidateTagsAsync(plan.Tags, runtime, cancellationToken).ConfigureAwait(false);
        else
            skipped += plan.Tags.Count;

        if (options.EnableZoneInvalidation)
            await InvalidateZonesAsync(plan.Zones, runtime, cancellationToken).ConfigureAwait(false);
        else
            skipped += plan.Zones.Count;

        if (options.EnableKeyInvalidation)
            await InvalidateKeysAsync(plan.Keys, runtime, cancellationToken).ConfigureAwait(false);
        else
            skipped += plan.Keys.Count;

        var result = new CacheInvalidationExecutionResult(
            RequestedTargets: plan.TotalTargets,
            InvalidatedTargets: invalidated,
            FailedTargets: failed,
            SkippedTargets: skipped,
            PolicyFailures: 0);

        if (result.HasFailures && runtime.ThrowOnFailure)
        {
            throw new CacheInvalidationExecutionException(
                "One or more cache invalidation operations failed.",
                result);
        }

        return result;

        async ValueTask InvalidateTagsAsync(
            IReadOnlyList<string> tags,
            CacheInvalidationRuntimeSettings settings,
            CancellationToken ct)
        {
            if (!settings.ExecuteTargetsInParallel || settings.MaxConcurrency <= 1 || tags.Count <= 1)
            {
                for (var i = 0; i < tags.Count; i++)
                {
                    try
                    {
                        _ = await _cache.InvalidateTagAsync(tags[i], ct).ConfigureAwait(false);
                        invalidated++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogInvalidationOperationFailed(_logger, ex, "tag", tags[i]);
                    }
                }

                return;
            }

            await Parallel.ForAsync(
                0,
                tags.Count,
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = settings.MaxConcurrency
                },
                async (index, token) =>
                {
                    var tag = tags[(int)index];
                    try
                    {
                        _ = await _cache.InvalidateTagAsync(tag, token).ConfigureAwait(false);
                        _ = Interlocked.Increment(ref invalidated);
                    }
                    catch (Exception ex)
                    {
                        _ = Interlocked.Increment(ref failed);
                        LogInvalidationOperationFailed(_logger, ex, "tag", tag);
                    }
                }).ConfigureAwait(false);
        }

        async ValueTask InvalidateZonesAsync(
            IReadOnlyList<string> zones,
            CacheInvalidationRuntimeSettings settings,
            CancellationToken ct)
        {
            if (!settings.ExecuteTargetsInParallel || settings.MaxConcurrency <= 1 || zones.Count <= 1)
            {
                for (var i = 0; i < zones.Count; i++)
                {
                    try
                    {
                        _ = await _cache.InvalidateZoneAsync(zones[i], ct).ConfigureAwait(false);
                        invalidated++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogInvalidationOperationFailed(_logger, ex, "zone", zones[i]);
                    }
                }

                return;
            }

            await Parallel.ForAsync(
                0,
                zones.Count,
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = settings.MaxConcurrency
                },
                async (index, token) =>
                {
                    var zone = zones[(int)index];
                    try
                    {
                        _ = await _cache.InvalidateZoneAsync(zone, token).ConfigureAwait(false);
                        _ = Interlocked.Increment(ref invalidated);
                    }
                    catch (Exception ex)
                    {
                        _ = Interlocked.Increment(ref failed);
                        LogInvalidationOperationFailed(_logger, ex, "zone", zone);
                    }
                }).ConfigureAwait(false);
        }

        async ValueTask InvalidateKeysAsync(
            IReadOnlyList<string> keys,
            CacheInvalidationRuntimeSettings settings,
            CancellationToken ct)
        {
            if (!settings.ExecuteTargetsInParallel || settings.MaxConcurrency <= 1 || keys.Count <= 1)
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    try
                    {
                        var removed = await _cache.RemoveAsync(new CacheKey(keys[i]), ct).ConfigureAwait(false);
                        if (removed)
                        {
                            invalidated++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogInvalidationOperationFailed(_logger, ex, "key", keys[i]);
                    }
                }

                return;
            }

            await Parallel.ForAsync(
                0,
                keys.Count,
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = settings.MaxConcurrency
                },
                async (index, token) =>
                {
                    var key = keys[(int)index];
                    try
                    {
                        var removed = await _cache.RemoveAsync(new CacheKey(key), token).ConfigureAwait(false);
                        if (removed)
                        {
                            _ = Interlocked.Increment(ref invalidated);
                        }
                        else
                        {
                            _ = Interlocked.Increment(ref skipped);
                        }
                    }
                    catch (Exception ex)
                    {
                        _ = Interlocked.Increment(ref failed);
                        LogInvalidationOperationFailed(_logger, ex, "key", key);
                    }
                }).ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 9101,
        Level = LogLevel.Debug,
        Message = "Cache invalidation execution skipped because the feature is disabled. RequestedTargets={RequestedTargets}")]
    private static partial void LogInvalidationDisabled(ILogger logger, int requestedTargets);

    [LoggerMessage(
        EventId = 9102,
        Level = LogLevel.Warning,
        Message = "Cache invalidation operation failed. TargetType={TargetType} TargetValue={TargetValue}")]
    private static partial void LogInvalidationOperationFailed(
        ILogger logger,
        Exception exception,
        string targetType,
        string targetValue);
}
