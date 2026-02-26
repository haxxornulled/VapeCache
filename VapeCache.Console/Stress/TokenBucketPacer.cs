using System.Threading.RateLimiting;

namespace VapeCache.Console.Stress;

internal sealed class TokenBucketPacer : IAsyncDisposable
{
    private readonly TokenBucketRateLimiter _limiter;

    public TokenBucketPacer(double targetRps, int burstRequests)
    {
        if (targetRps <= 0) throw new ArgumentOutOfRangeException(nameof(targetRps));
        if (burstRequests <= 0) throw new ArgumentOutOfRangeException(nameof(burstRequests));

        // Choose a replenishment period that keeps integer math accurate and timer overhead low.
        // 100ms works well up to very high RPS (e.g., 10k RPS => 1000 tokens/period).
        var period = TimeSpan.FromMilliseconds(100);
        var tokensPerPeriod = Math.Max(1, (int)Math.Round(targetRps * period.TotalSeconds, MidpointRounding.AwayFromZero));

        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = burstRequests,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = period,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = Math.Clamp(burstRequests * 4, 1024, 1_000_000),
            AutoReplenishment = true
        });
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        using var lease = await _limiter.AcquireAsync(1, ct).ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new TimeoutException("Rate limiter queue full.");
    }

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _limiter.Dispose();
        return ValueTask.CompletedTask;
    }
}

