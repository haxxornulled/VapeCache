using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Hosting;
using VapeCache.Console.Stress;

namespace VapeCache.Tests.ConsoleHosting;

public sealed class RedisConnectionPoolReaperHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_runs_reaper_in_pool_mode()
    {
        var reaper = new TestReaper();

        var options = Options.Create(new RedisStressOptions { Mode = "pool" });
        var sut = new RedisConnectionPoolReaperHostedService(
            options,
            reaper,
            NullLogger<RedisConnectionPoolReaperHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(40);
        await sut.StopAsync(CancellationToken.None);

        Assert.True(reaper.Calls >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_skips_reaper_for_non_pool_mode()
    {
        var reaper = new TestReaper();

        var options = Options.Create(new RedisStressOptions { Mode = "mux" });
        var sut = new RedisConnectionPoolReaperHostedService(
            options,
            reaper,
            NullLogger<RedisConnectionPoolReaperHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(40);
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(0, reaper.Calls);
    }

    private sealed class TestReaper : IRedisConnectionPoolReaper
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task RunReaperAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }
}
