using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class PendingOperationLifecycleTests
{
    [Fact]
    public async Task Complete_vs_timeout_race_completes_exactly_once_and_recycles_after_observe()
    {
        var inFlight = new SemaphoreSlim(1, 1);
        var pool = new PendingOperationPool(CancellationToken.None, inFlight, null, null);

        for (var i = 0; i < 1_000; i++)
        {
            var op = pool.Rent();
            op.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: i + 1);
            var valueTask = op.ValueTask;
            var timeout = new TimeoutException("timeout");

            var resultTask = Task.Run(() => op.TrySetResult(RedisRespReader.RespValue.Integer(i)));
            var timeoutTask = Task.Run(() => op.TrySetException(timeout));
            await Task.WhenAll(resultTask, timeoutTask);
            op.MarkResponseProcessed();

            try
            {
                var resp = await valueTask;
                Assert.Equal(RedisRespReader.RespKind.Integer, resp.Kind);
            }
            catch (TimeoutException ex)
            {
                Assert.Same(timeout, ex);
            }

            Assert.True(WaitForPoolItem(pool, out var returned), $"Operation was not returned to pool at iteration {i}.");
            Assert.Same(op, returned);
        }
    }

    [Fact]
    public async Task Complete_vs_cancel_race_completes_exactly_once_and_recycles_after_observe()
    {
        var inFlight = new SemaphoreSlim(1, 1);
        var pool = new PendingOperationPool(CancellationToken.None, inFlight, null, null);

        for (var i = 0; i < 1_000; i++)
        {
            using var cts = new CancellationTokenSource();
            var op = pool.Rent();
            op.Start(poolBulk: false, ct: cts.Token, holdsSlot: false, sequenceId: i + 1);
            var valueTask = op.ValueTask;

            var completeTask = Task.Run(() => op.TrySetResult(RedisRespReader.RespValue.SimpleString("ok")));
            var cancelTask = Task.Run(cts.Cancel);
            await Task.WhenAll(completeTask, cancelTask);
            op.MarkResponseProcessed();

            try
            {
                var resp = await valueTask;
                Assert.Equal(RedisRespReader.RespKind.SimpleString, resp.Kind);
                Assert.Equal("ok", resp.Text);
            }
            catch (OperationCanceledException)
            {
                // Cancellation can win the race.
            }

            Assert.True(WaitForPoolItem(pool, out var returned), $"Operation was not returned to pool at iteration {i}.");
            Assert.Same(op, returned);
        }
    }

    [Fact]
    public async Task Operation_is_not_reused_before_consumer_observes_completion()
    {
        var inFlight = new SemaphoreSlim(1, 1);
        var pool = new PendingOperationPool(CancellationToken.None, inFlight, null, null);

        var first = pool.Rent();
        first.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: 1);
        var firstTask = first.ValueTask;

        first.TrySetException(new InvalidOperationException("first"));
        first.MarkResponseProcessed();

        Assert.False(pool.TryTake(out _));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstTask);

        Assert.True(WaitForPoolItem(pool, out var returned));
        Assert.Same(first, returned);
    }

    [Fact]
    public async Task Duplicate_mark_processed_does_not_return_same_operation_twice()
    {
        var inFlight = new SemaphoreSlim(1, 1);
        var pool = new PendingOperationPool(CancellationToken.None, inFlight, null, null);

        var op = pool.Rent();
        op.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: 1);

        op.TrySetResult(RedisRespReader.RespValue.Integer(42));
        op.MarkResponseProcessed();
        op.MarkResponseProcessed();

        var resp = await op.ValueTask;
        Assert.Equal(42, resp.IntegerValue);

        Assert.True(WaitForPoolItem(pool, out var returned));
        Assert.Same(op, returned);
        Assert.False(pool.TryTake(out _));
    }

    [Fact]
    public async Task Reused_operation_ignores_stale_caller_cancel_callback_when_current_token_is_none()
    {
        var inFlight = new SemaphoreSlim(1, 1);
        var pool = new PendingOperationPool(CancellationToken.None, inFlight, null, null);
        var staleCancelCallback = typeof(PendingOperation).GetMethod(
            "TrySetCanceledFromCallerCallback",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(staleCancelCallback);

        var first = pool.Rent();
        first.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: 1);
        first.TrySetResult(RedisRespReader.RespValue.Integer(1));
        first.MarkResponseProcessed();
        var firstResponse = await first.ValueTask;
        Assert.Equal(1, firstResponse.IntegerValue);

        var reused = pool.Rent();
        Assert.Same(first, reused);

        reused.Start(poolBulk: false, ct: CancellationToken.None, holdsSlot: false, sequenceId: 2);
        var secondTask = reused.ValueTask.AsTask();

        // Simulate a late callback from a prior operation generation.
        staleCancelCallback!.Invoke(reused, null);

        reused.TrySetResult(RedisRespReader.RespValue.Integer(2));
        reused.MarkResponseProcessed();

        var secondResponse = await secondTask;
        Assert.Equal(RedisRespReader.RespKind.Integer, secondResponse.Kind);
        Assert.Equal(2, secondResponse.IntegerValue);

        Assert.True(WaitForPoolItem(pool, out var returned));
        Assert.Same(reused, returned);
    }

    private static bool WaitForPoolItem(PendingOperationPool pool, out PendingOperation? operation)
    {
        operation = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (pool.TryTake(out operation))
                return true;

            Thread.Yield();
        }

        return false;
    }
}
