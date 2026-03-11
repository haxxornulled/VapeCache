using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Wraps a RedisConnectionFactory with Polly Circuit Breaker Pattern.
/// Tracks connection-level failures and opens circuit when Redis is unavailable.
/// Triggers reconciliation service to sync in-memory writes back to Redis on recovery.
/// </summary>
internal sealed partial class CircuitBreakerRedisConnectionFactory : IRedisConnectionFactory
{
    private readonly IRedisConnectionFactory _inner;
    private readonly ResiliencePipeline<Result<IRedisConnection>> _pipeline;
    private readonly ILogger<CircuitBreakerRedisConnectionFactory> _logger;
    private readonly CacheStats _stats;
    private readonly IOptionsMonitor<RedisCircuitBreakerOptions> _optionsMonitor;
    private RedisCircuitBreakerOptions _options => _optionsMonitor.CurrentValue;
    private readonly System.Threading.Lock _stateGate = new();
    private CircuitState _currentState = CircuitState.Closed;
    private int _consecutiveRetries;
    private TimeSpan _currentBreakDuration;

    public CircuitBreakerRedisConnectionFactory(
        RedisConnectionFactory inner, // Concrete type to avoid circular dep with IRedisConnectionFactory
        IOptionsMonitor<RedisCircuitBreakerOptions> options,
        CacheStatsRegistry statsRegistry,
        ILogger<CircuitBreakerRedisConnectionFactory> logger)
    {
        _inner = inner;
        _optionsMonitor = options;
        _stats = statsRegistry.GetOrCreate(CacheStatsNames.Hybrid);
        _logger = logger;
        _currentBreakDuration = _options.BreakDuration; // Initialize with base duration

        // Build Polly resilience pipeline with circuit breaker that handles Result<T>
        // This is the PROPER way to use Polly Circuit Breaker Pattern with Result-based APIs
        _pipeline = new ResiliencePipelineBuilder<Result<IRedisConnection>>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<Result<IRedisConnection>>
            {
                // Treat failed Result<T> as failures (not just exceptions)
                ShouldHandle = new PredicateBuilder<Result<IRedisConnection>>()
                    .HandleResult(r => r.IsFaulted), // Result is a failure if it contains an exception

                // AGGRESSIVE circuit breaker for developer experience:
                // Open immediately after N consecutive failures (no long sampling window)
                // Developers shouldn't wait 30 seconds for their app to work!
                FailureRatio = 1.0, // Open circuit if 100% of requests fail
                SamplingDuration = TimeSpan.FromSeconds(2), // Short 2-second window for fast detection
                MinimumThroughput = Math.Max(2, _options.ConsecutiveFailuresToOpen), // Polly requires minimum 2

                // Dynamic break duration with exponential backoff support
                BreakDurationGenerator = args =>
                {
                    lock (_stateGate)
                    {
                        var optionsSnapshot = _options;
                        if (optionsSnapshot.MaxConsecutiveRetries > 0 && _consecutiveRetries >= optionsSnapshot.MaxConsecutiveRetries)
                        {
                            LogMaxRetriesExceeded(_logger, optionsSnapshot.MaxConsecutiveRetries);
                            // Keep open effectively forever until operator intervention.
                            return ValueTask.FromResult(TimeSpan.FromDays(365));
                        }

                        return ValueTask.FromResult(_currentBreakDuration);
                    }
                },
                OnOpened = args =>
                {
                    int retries;
                    TimeSpan duration;
                    CircuitState previous;
                    CircuitState current;
                    lock (_stateGate)
                    {
                        previous = _currentState;
                        _currentState = CircuitState.Open;
                        current = _currentState;
                        _stats.IncBreakerOpened();
                        CacheTelemetry.RedisBreakerOpened.Add(1, new System.Diagnostics.TagList { { "backend", "hybrid" } });

                        _consecutiveRetries++;
                        retries = _consecutiveRetries;

                        var optionsSnapshot = _options;
                        if (optionsSnapshot.UseExponentialBackoff)
                        {
                            var doubled = TimeSpan.FromMilliseconds(_currentBreakDuration.TotalMilliseconds * 2);
                            _currentBreakDuration = doubled > optionsSnapshot.MaxBreakDuration
                                ? optionsSnapshot.MaxBreakDuration
                                : doubled;
                        }

                        duration = _currentBreakDuration;
                    }

                    LogCircuitOpened(_logger, retries, duration.TotalSeconds, previous, current);

                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    var optionsSnapshot = _options;
                    int totalRetries;
                    CircuitState previous;
                    CircuitState current;
                    lock (_stateGate)
                    {
                        previous = _currentState;
                        _currentState = CircuitState.Closed;
                        current = _currentState;
                        totalRetries = _consecutiveRetries;
                        _consecutiveRetries = 0;
                        _currentBreakDuration = optionsSnapshot.BreakDuration;
                    }

                    LogCircuitClosed(_logger, totalRetries, previous, current);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    CircuitState previous;
                    CircuitState current;
                    lock (_stateGate)
                    {
                        previous = _currentState;
                        _currentState = CircuitState.HalfOpen;
                        current = _currentState;
                    }
                    LogCircuitHalfOpen(_logger, previous, current);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public async ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
            return await _inner.CreateAsync(ct).ConfigureAwait(false);

        try
        {
            // Execute connection creation through circuit breaker
            return await _pipeline.ExecuteAsync(
                async token => await _inner.CreateAsync(token).ConfigureAwait(false),
                ct).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            // Circuit is open - return a failure Result
            LogConnectionBlockedOpenCircuit(_logger);
            return new Result<IRedisConnection>(ex);
        }
    }

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    [LoggerMessage(
        EventId = 24100,
        Level = LogLevel.Error,
        Message = "Circuit breaker: Max retries ({MaxRetries}) exceeded. Staying in OPEN state indefinitely.")]
    private static partial void LogMaxRetriesExceeded(ILogger logger, int maxRetries);

    [LoggerMessage(
        EventId = 24101,
        Level = LogLevel.Warning,
        Message = "Circuit breaker OPENED - Redis connections failing (retry #{Retry}). Switching to in-memory mode for {Duration} seconds. State: {Previous}->{Current}")]
    private static partial void LogCircuitOpened(
        ILogger logger,
        int retry,
        double duration,
        CircuitState previous,
        CircuitState current);

    [LoggerMessage(
        EventId = 24102,
        Level = LogLevel.Information,
        Message = "Circuit breaker CLOSED. Redis operations resumed after {Retries} retries. State: {Previous}->{Current}")]
    private static partial void LogCircuitClosed(
        ILogger logger,
        int retries,
        CircuitState previous,
        CircuitState current);

    [LoggerMessage(
        EventId = 24103,
        Level = LogLevel.Information,
        Message = "Circuit breaker HALF-OPEN. Testing Redis connection. State: {Previous}->{Current}")]
    private static partial void LogCircuitHalfOpen(ILogger logger, CircuitState previous, CircuitState current);

    [LoggerMessage(
        EventId = 24104,
        Level = LogLevel.Debug,
        Message = "Circuit breaker is open, connection creation blocked")]
    private static partial void LogConnectionBlockedOpenCircuit(ILogger logger);
}

