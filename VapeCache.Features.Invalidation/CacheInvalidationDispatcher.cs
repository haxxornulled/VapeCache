using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VapeCache.Features.Invalidation;

/// <summary>
/// Dispatches events through registered invalidation policies and executes the merged plan.
/// </summary>
public sealed partial class CacheInvalidationDispatcher(
    IServiceProvider serviceProvider,
    ICacheInvalidationExecutor executor,
    IOptionsMonitor<CacheInvalidationOptions> optionsMonitor,
    ILogger<CacheInvalidationDispatcher> logger) : ICacheInvalidationDispatcher
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ICacheInvalidationExecutor _executor = executor;
    private readonly IOptionsMonitor<CacheInvalidationOptions> _optionsMonitor = optionsMonitor;
    private readonly ILogger<CacheInvalidationDispatcher> _logger = logger;

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public async ValueTask<CacheInvalidationExecutionResult> DispatchAsync<TEvent>(
        TEvent eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = _optionsMonitor.CurrentValue;
        var runtime = options.ResolveRuntimeSettings();
        var resolvedPolicies = _serviceProvider.GetServices<ICacheInvalidationPolicy<TEvent>>();
        var policies = resolvedPolicies as IReadOnlyList<ICacheInvalidationPolicy<TEvent>>
            ?? resolvedPolicies.ToArray();
        if (policies.Count == 0)
            return default;

        var builder = new CacheInvalidationPlanBuilder();
        var policyFailures = 0;

        if (!runtime.EvaluatePoliciesInParallel || runtime.MaxConcurrency <= 1 || policies.Count <= 1)
        {
            for (var i = 0; i < policies.Count; i++)
            {
                try
                {
                    var plan = await policies[i].BuildPlanAsync(eventData, cancellationToken).ConfigureAwait(false);
                    builder.AddPlan(plan);
                }
                catch (Exception ex)
                {
                    policyFailures++;
                    LogInvalidationPolicyFailed(_logger, ex, typeof(TEvent).Name, policies[i].GetType().Name);
                    if (runtime.ThrowOnFailure)
                    {
                        throw new CacheInvalidationExecutionException(
                            "A cache invalidation policy failed while strict mode is enabled.",
                            new CacheInvalidationExecutionResult(
                                RequestedTargets: 0,
                                InvalidatedTargets: 0,
                                FailedTargets: 0,
                                SkippedTargets: 0,
                                PolicyFailures: policyFailures));
                    }
                }
            }
        }
        else
        {
            var plans = new CacheInvalidationPlan[policies.Count];
            await Parallel.ForAsync(
                0,
                policies.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = runtime.MaxConcurrency
                },
                async (index, ct) =>
                {
                    var policyIndex = (int)index;
                    try
                    {
                        plans[policyIndex] = await policies[policyIndex].BuildPlanAsync(eventData, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _ = Interlocked.Increment(ref policyFailures);
                        LogInvalidationPolicyFailed(_logger, ex, typeof(TEvent).Name, policies[policyIndex].GetType().Name);
                    }
                }).ConfigureAwait(false);

            for (var i = 0; i < plans.Length; i++)
            {
                builder.AddPlan(plans[i] ?? CacheInvalidationPlan.Empty);
            }

            if (policyFailures > 0 && runtime.ThrowOnFailure)
            {
                throw new CacheInvalidationExecutionException(
                    "One or more cache invalidation policies failed while strict mode is enabled.",
                    new CacheInvalidationExecutionResult(
                        RequestedTargets: 0,
                        InvalidatedTargets: 0,
                        FailedTargets: 0,
                        SkippedTargets: 0,
                        PolicyFailures: policyFailures));
            }
        }

        var executionResult = await _executor.InvalidateAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        if (policyFailures == 0)
            return executionResult;

        return executionResult with { PolicyFailures = policyFailures };
    }

    [LoggerMessage(
        EventId = 9111,
        Level = LogLevel.Warning,
        Message = "Cache invalidation policy failed. EventType={EventType} PolicyType={PolicyType}")]
    private static partial void LogInvalidationPolicyFailed(
        ILogger logger,
        Exception exception,
        string eventType,
        string policyType);
}
