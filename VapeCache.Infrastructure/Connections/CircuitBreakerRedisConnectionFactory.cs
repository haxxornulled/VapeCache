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
internal sealed class CircuitBreakerRedisConnectionFactory : IRedisConnectionFactory
{
    private readonly IRedisConnectionFactory _inner;
    private readonly ResiliencePipeline<Result<IRedisConnection>> _pipeline;
    private readonly ILogger<CircuitBreakerRedisConnectionFactory> _logger;
    private readonly CacheStats _stats;
    private readonly RedisCircuitBreakerOptions _options;
    private CircuitState _currentState = CircuitState.Closed;
    private int _consecutiveRetries = 0;
    private TimeSpan _currentBreakDuration;

    public CircuitBreakerRedisConnectionFactory(
        RedisConnectionFactory inner, // Concrete type to avoid circular dep with IRedisConnectionFactory
        IOptions<RedisCircuitBreakerOptions> options,
        CacheStatsRegistry statsRegistry,
        ILogger<CircuitBreakerRedisConnectionFactory> logger)
    {
        _inner = inner;
        _options = options.Value;
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
                    var duration = _currentBreakDuration;

                    // Check if max retries exceeded (if configured)
                    if (_options.MaxConsecutiveRetries > 0 && _consecutiveRetries >= _options.MaxConsecutiveRetries)
                    {
                        _logger.LogError(
                            "Circuit breaker: Max retries ({MaxRetries}) exceeded. Staying in OPEN state indefinitely.",
                            _options.MaxConsecutiveRetries);
                        // Return a very long duration to effectively stay open
                        return ValueTask.FromResult(TimeSpan.FromDays(365));
                    }

                    return ValueTask.FromResult(duration);
                },
                OnOpened = args =>
                {
                    var previous = _currentState;
                    _currentState = CircuitState.Open;
                    _stats.IncBreakerOpened();
                    CacheTelemetry.RedisBreakerOpened.Add(1, new System.Diagnostics.TagList { { "backend", "hybrid" } });

                    // Increment retry counter
                    _consecutiveRetries++;

                    // Apply exponential backoff if enabled
                    if (_options.UseExponentialBackoff)
                    {
                        var newDuration = TimeSpan.FromMilliseconds(_currentBreakDuration.TotalMilliseconds * 2);
                        _currentBreakDuration = newDuration > _options.MaxBreakDuration
                            ? _options.MaxBreakDuration
                            : newDuration;
                    }

                    _logger.LogWarning(
                        "⚡ CIRCUIT BREAKER OPENED - Redis connections failing (retry #{Retry}). Switching to in-memory mode for {Duration} seconds. State: {Previous}->{Current}",
                        _consecutiveRetries,
                        _currentBreakDuration.TotalSeconds,
                        previous,
                        _currentState);

                    Console.WriteLine(
                        $"\n🔥🔥🔥 ⚡ CIRCUIT BREAKER OPENED! Redis connections failing (retry #{_consecutiveRetries}), switching to IN-MEMORY mode for {_currentBreakDuration.TotalSeconds} seconds 🔥🔥🔥\n");

                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    var previous = _currentState;
                    _currentState = CircuitState.Closed;

                    // Reset retry counter and break duration on successful recovery
                    var totalRetries = _consecutiveRetries;
                    _consecutiveRetries = 0;
                    _currentBreakDuration = _options.BreakDuration;

                    _logger.LogInformation(
                        "✅ Circuit breaker CLOSED. Redis operations resumed after {Retries} retries. State: {Previous}->{Current}",
                        totalRetries,
                        previous,
                        _currentState);
                    Console.WriteLine(
                        $"\n✅✅✅ Circuit breaker CLOSED! Redis operations resumed after {totalRetries} retries. ✅✅✅\n");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    var previous = _currentState;
                    _currentState = CircuitState.HalfOpen;
                    _logger.LogInformation("🔄 Circuit breaker HALF-OPEN. Testing Redis connection... State: {Previous}->{Current}", previous, _currentState);
                    Console.WriteLine("\n🔄 Circuit breaker HALF-OPEN. Testing Redis connection...\n");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

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
            _logger.LogDebug("Circuit breaker is open, connection creation blocked");
            return new Result<IRedisConnection>(ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }
}

