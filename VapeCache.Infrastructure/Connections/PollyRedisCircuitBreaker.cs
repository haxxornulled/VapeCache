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
internal sealed partial class PollyRedisCircuitBreaker : IRedisCircuitBreakerState, IRedisFailoverController
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

                    LogCircuitOpened(_logger, _options.BreakDuration.TotalSeconds);
                    LogReconciliationStoreWarning(_logger);

                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    _currentState = CircuitState.Closed;
                    LogCircuitClosed(_logger);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    _currentState = CircuitState.HalfOpen;
                    LogCircuitHalfOpen(_logger);
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

    /// <summary>
    /// Executes value.
    /// </summary>
    public void MarkRedisSuccess()
    {
        // Polly handles success tracking automatically
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void MarkRedisFailure()
    {
        // Polly handles failure tracking automatically when exceptions are thrown
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void ForceOpen(string reason)
    {
        if (!_options.Enabled) return;
        _forcedReason = reason;
        _currentState = CircuitState.Open;
        LogForcedOpen(_logger, reason);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public void ClearForcedOpen()
    {
        if (!_options.Enabled) return;
        _forcedReason = null;
        _currentState = CircuitState.Closed;
        LogManualOverrideCleared(_logger);
    }

    [LoggerMessage(
        EventId = 24000,
        Level = LogLevel.Warning,
        Message = "Circuit breaker opened after failures. Switching to in-memory mode for {Duration} seconds.")]
    private static partial void LogCircuitOpened(ILogger logger, double duration);

    [LoggerMessage(
        EventId = 24001,
        Level = LogLevel.Warning,
        Message = "Cache writes during this outage are not reconciled back to Redis without a reconciliation store.")]
    private static partial void LogReconciliationStoreWarning(ILogger logger);

    [LoggerMessage(
        EventId = 24002,
        Level = LogLevel.Information,
        Message = "Circuit breaker closed. Redis operations resumed.")]
    private static partial void LogCircuitClosed(ILogger logger);

    [LoggerMessage(
        EventId = 24003,
        Level = LogLevel.Information,
        Message = "Circuit breaker half-open. Testing Redis connection.")]
    private static partial void LogCircuitHalfOpen(ILogger logger);

    [LoggerMessage(
        EventId = 24004,
        Level = LogLevel.Warning,
        Message = "Circuit breaker manually forced open: {Reason}")]
    private static partial void LogForcedOpen(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 24005,
        Level = LogLevel.Information,
        Message = "Circuit breaker manual override cleared")]
    private static partial void LogManualOverrideCleared(ILogger logger);
}

