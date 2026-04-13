using Moq;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Modules;

namespace VapeCache.Tests.Modules;

public sealed class RedisModuleDetectorTests
{
    [Fact]
    public async Task GetInstalledModulesAsync_caches_first_result()
    {
        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        mock.Setup(m => m.ModuleListAsync(It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new[] { "ReJSON", "search" }));

        var sut = new RedisModuleDetector(mock.Object);

        var first = await sut.GetInstalledModulesAsync();
        var second = await sut.GetInstalledModulesAsync();

        Assert.Equal(2, first.Length);
        Assert.Equal(first, second);
        mock.Verify(m => m.ModuleListAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HasRedisJsonAsync_uses_cached_modules()
    {
        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        mock.Setup(m => m.ModuleListAsync(It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new[] { "ReJSON" }));

        var sut = new RedisModuleDetector(mock.Object);

        Assert.True(await sut.HasRedisJsonAsync());
        Assert.True(await sut.IsModuleInstalledAsync("rejson"));
        mock.Verify(m => m.ModuleListAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetInstalledModulesAsync_returns_empty_when_module_list_fails()
    {
        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        mock.Setup(m => m.ModuleListAsync(It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromException<string[]>(new InvalidOperationException("MODULE LIST unavailable")));

        var sut = new RedisModuleDetector(mock.Object);

        var modules = await sut.GetInstalledModulesAsync();

        Assert.Empty(modules);
    }

    [Fact]
    public async Task GetInstalledModulesAsync_retries_after_transient_failure()
    {
        var attempts = 0;
        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        mock.Setup(m => m.ModuleListAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException<string[]>(new InvalidOperationException("transient"))
                    : ValueTask.FromResult(new[] { "search" });
            });

        var sut = new RedisModuleDetector(mock.Object, TimeProvider.System, TimeSpan.Zero);

        var first = await sut.GetInstalledModulesAsync();
        var second = await sut.GetInstalledModulesAsync();

        Assert.Empty(first);
        Assert.Single(second);
        Assert.Equal("search", second[0]);
        mock.Verify(m => m.ModuleListAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetInstalledModulesAsync_propagates_user_cancellation()
    {
        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        mock.Setup(m => m.ModuleListAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromException<string[]>(new OperationCanceledException(ct));
            });

        var sut = new RedisModuleDetector(mock.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetInstalledModulesAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task GetInstalledModulesAsync_coalesces_concurrent_calls()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        var mock = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        mock.Setup(m => m.ModuleListAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref attempts);
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return ["ReJSON", "search"];
            });

        var sut = new RedisModuleDetector(mock.Object);
        var first = sut.GetInstalledModulesAsync().AsTask();
        await started.Task;

        var concurrent = Enumerable.Range(0, 15)
            .Select(_ => sut.GetInstalledModulesAsync().AsTask())
            .ToArray();

        release.TrySetResult();

        var all = await Task.WhenAll([first, ..concurrent]);
        Assert.All(all, modules => Assert.Equal(["ReJSON", "search"], modules));
        Assert.Equal(1, attempts);
        mock.Verify(m => m.ModuleListAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
