using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Features.Invalidation;

public sealed class CacheInvalidationDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ContinuesOnPolicyFailure_ForSmallWebsiteProfile()
    {
        var services = new ServiceCollection();
        services.AddOptions<CacheInvalidationOptions>()
            .Configure(options => options.Profile = CacheInvalidationProfile.SmallWebsite);
        services.AddSingleton<ICacheInvalidationExecutor>(new RecordingExecutor());
        services.AddSingleton<ICacheInvalidationPolicy<OrderEvent>>(new ThrowingPolicy());
        services.AddSingleton<ICacheInvalidationPolicy<OrderEvent>>(
            new StaticInvalidationPolicy<OrderEvent>(new CacheInvalidationPlan(tags: ["order:1"])));
        services.AddSingleton<ICacheInvalidationDispatcher, CacheInvalidationDispatcher>();
        services.AddSingleton<ILogger<CacheInvalidationDispatcher>>(NullLogger<CacheInvalidationDispatcher>.Instance);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        var result = await dispatcher.DispatchAsync(new OrderEvent("1"));

        Assert.Equal(1, result.RequestedTargets);
        Assert.Equal(1, result.InvalidatedTargets);
        Assert.Equal(1, result.PolicyFailures);
    }

    [Fact]
    public async Task DispatchAsync_ThrowsOnPolicyFailure_ForHighTrafficProfile()
    {
        var services = new ServiceCollection();
        services.AddOptions<CacheInvalidationOptions>()
            .Configure(options => options.Profile = CacheInvalidationProfile.HighTrafficSite);
        services.AddSingleton<ICacheInvalidationExecutor>(new RecordingExecutor());
        services.AddSingleton<ICacheInvalidationPolicy<OrderEvent>>(new ThrowingPolicy());
        services.AddSingleton<ICacheInvalidationDispatcher, CacheInvalidationDispatcher>();
        services.AddSingleton<ILogger<CacheInvalidationDispatcher>>(NullLogger<CacheInvalidationDispatcher>.Instance);

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ICacheInvalidationDispatcher>();

        _ = await Assert.ThrowsAsync<CacheInvalidationExecutionException>(async () =>
        {
            _ = await dispatcher.DispatchAsync(new OrderEvent("1"));
        });
    }

    private sealed class RecordingExecutor : ICacheInvalidationExecutor
    {
        public ValueTask<CacheInvalidationExecutionResult> InvalidateAsync(
            CacheInvalidationPlan plan,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CacheInvalidationExecutionResult(
                RequestedTargets: plan.TotalTargets,
                InvalidatedTargets: plan.TotalTargets,
                FailedTargets: 0,
                SkippedTargets: 0,
                PolicyFailures: 0));
    }

    private sealed class ThrowingPolicy : ICacheInvalidationPolicy<OrderEvent>
    {
        public ValueTask<CacheInvalidationPlan> BuildPlanAsync(OrderEvent eventData, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("policy failure");
    }

    private sealed record OrderEvent(string Id);
}
