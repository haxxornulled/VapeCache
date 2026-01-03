using System.Threading.Tasks;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class RedisBatchTests
{
    [Fact]
    public async Task ExecuteAsync_Awaits_PendingOperations()
    {
        var executor = new InMemoryCommandExecutor();
        await using var batch = new RedisBatch(executor);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = batch.QueueAsync((_, _) => new ValueTask(tcs.Task));

        var executeTask = batch.ExecuteAsync();
        Assert.False(executeTask.IsCompleted);

        tcs.SetResult();
        await executeTask;
    }

    [Fact]
    public async Task QueueAsync_Returns_Result()
    {
        var executor = new InMemoryCommandExecutor();
        await using var batch = new RedisBatch(executor);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = batch.QueueAsync<int>((_, _) => new ValueTask<int>(tcs.Task));

        var executeTask = batch.ExecuteAsync();
        tcs.SetResult(42);

        await executeTask;
        Assert.Equal(42, await queued);
    }
}
