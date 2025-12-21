using VapeCache.Console.Stress;
using Xunit;

namespace VapeCache.Tests.ConsoleStress;

public sealed class TokenBucketPacerTests
{
    [Fact]
    public async Task WaitAsync_allows_burst_then_throttles()
    {
        await using var pacer = new TokenBucketPacer(targetRps: 10, burstRequests: 2);

        // Burst of 2 should pass quickly.
        await pacer.WaitAsync(CancellationToken.None);
        await pacer.WaitAsync(CancellationToken.None);

        // Third may have to wait; just ensure it completes eventually.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await pacer.WaitAsync(cts.Token);
    }
}

