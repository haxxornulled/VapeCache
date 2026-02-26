using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Polly-based circuit breaker for Redis operations.
/// Implements proper Circuit Breaker Pattern following Polly best practices.
/// </summary>
internal sealed class PollyRedisCircuitBreaker : IRedisCircuitBreakerState, IRedisFailoverController
{
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<PollyRedisCircuitBreaker> _logger;
    private readonly CacheStats _stats;
    private readonly IOptionsMonitor<RedisCircuitBreakerOptions> _optionsMonitor;
    private RedisCircuitBreakerOptions _options => _optionsMonitor.CurrentValue;
    private CircuitState _currentState = CircuitState.Closed;
    private string? _forcedReason;

    public PollyRedisCircuitBreaker(
        IOptionsMonitor<RedisCircuitBreakerOptions> options,
        CacheStatsRegistry statsRegistry,
        ILogger<PollyRedisCircuitBreaker> logger)
    {
        _optionsMonitor = options;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
        _logger = logger;

        // Build Polly resilience pipeline with circuit breaker
        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5, // Open circuit if 50% of requests fail
                SamplingDuration = TimeSpan.FromSeconds(2), // Sample window
                MinimumThroughput = _options.ConsecutiveFailuresToOpen, // Minimum requests before considering failure ratio
                BreakDuration = _options.BreakDuration,
                OnOpened = args =>
                {
                    _currentState = CircuitState.Open;
                    _stats.IncBreakerOpened();
                    CacheTelemetry.RedisBreakerOpened.Add(1, new System.Diagnostics.TagList { { "backend", "hybrid" } });

                    _logger.LogWarning(
                        "Circuit breaker opened after failures. Switching to in-memory mode for {Duration} seconds.",
                        _options.BreakDuration.TotalSeconds);
                    _logger.LogWarning(
                        "Cache writes during this outage are not reconciled back to Redis without a reconciliation store.");

                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    _currentState = CircuitState.Closed;
                    _logger.LogInformation("Circuit breaker closed. Redis operations resumed.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    _currentState = CircuitState.HalfOpen;
                    _logger.LogInformation("Circuit breaker half-open. Testing Redis connection.");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public bool Enabled => _options.Enabled;

    public bool IsOpen => _options.Enabled && (_currentState == CircuitState.Open || IsForcedOpen);

    public int ConsecutiveFailures => 0; // Polly tracks this internally

    public TimeSpan? OpenRemaining => null; // Polly manages this internally

    public bool HalfOpenProbeInFlight => _currentState == CircuitState.HalfOpen;

    public bool IsForcedOpen => !string.IsNullOrEmpty(_forcedReason);

    public string? Reason => _forcedReason;

    /// <summary>
    /// Execute a Redis operation with circuit breaker protection.
    /// </summary>
    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken ct)
    {
        if (!_options.Enabled)
            return await operation(ct).ConfigureAwait(false);

        if (IsForcedOpen)
            throw new BrokenCircuitException($"Circuit breaker is manually forced open: {_forcedReason}");

        return await _pipeline.ExecuteAsync(
            async token => await operation(token).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public void MarkRedisSuccess()
    {
        // Polly handles success tracking automatically
    }

    public void MarkRedisFailure()
    {
        // Polly handles failure tracking automatically when exceptions are thrown
    }

    public void ForceOpen(string reason)
    {
        if (!_options.Enabled) return;
        _forcedReason = reason;
        _currentState = CircuitState.Open;
        _logger.LogWarning("Circuit breaker manually forced open: {Reason}", reason);
    }

    public void ClearForcedOpen()
    {
        if (!_options.Enabled) return;
        _forcedReason = null;
        _currentState = CircuitState.Closed;
        _logger.LogInformation("Circuit breaker manual override cleared");
    }
}

