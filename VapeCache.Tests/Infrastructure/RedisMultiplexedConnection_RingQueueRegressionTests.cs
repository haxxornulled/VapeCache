using System.Reflection;
using LanguageExt.Common;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Infrastructure;

public sealed class RedisMultiplexedConnection_RingQueueRegressionTests
{
    [Fact]
    public async Task Ctor_RoundsCapacityToPowerOfTwo()
    {
        // _maxInFlight * 4 = 300 (not a power of two) previously threw.
        await using var conn = new RedisMultiplexedConnection(new FailingFactory(), maxInFlight: 75, coalesceWrites: false);
        // If construction succeeds, this regression is fixed. Dispose should also not hang.
    }

    [Fact]
    public async Task MpscRingQueue_EnqueueAsync_SynchronousWait_PublishesItem()
    {
        var queue = CreatePrivateNestedQueue("MpscRingQueue`1", typeof(int), capacity: 8);
        await InvokeEnqueueAsync(queue, item: 123);
        var dequeued = await InvokeDequeueAsync(queue);
        Assert.Equal(123, dequeued);
    }

    [Fact]
    public async Task SpscRingQueue_EnqueueAsync_SynchronousWait_PublishesItem()
    {
        var queue = CreatePrivateNestedQueue("SpscRingQueue`1", typeof(int), capacity: 8);
        await InvokeEnqueueAsync(queue, item: 456);
        var dequeued = await InvokeDequeueAsync(queue);
        Assert.Equal(456, dequeued);
    }

    private static object CreatePrivateNestedQueue(string nestedGenericTypeName, Type elementType, int capacity)
    {
        var owner = typeof(RedisMultiplexedConnection);
        var nested = owner.GetNestedType(nestedGenericTypeName, BindingFlags.NonPublic);
        Assert.NotNull(nested);

        var constructed = nested!.MakeGenericType(elementType);
        var instance = Activator.CreateInstance(constructed, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null, args: new object[] { capacity }, culture: null);
        Assert.NotNull(instance);
        return instance!;
    }

    private static async Task InvokeEnqueueAsync(object queue, int item)
    {
        var method = queue.GetType().GetMethod("EnqueueAsync", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var vt = (ValueTask)method!.Invoke(queue, new object?[] { item, CancellationToken.None })!;
        await vt;
    }

    private static async Task<int> InvokeDequeueAsync(object queue)
    {
        var method = queue.GetType().GetMethod("DequeueAsync", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var vtObj = method!.Invoke(queue, new object?[] { CancellationToken.None })!;

        // ValueTask<T> is a struct; reflection returns boxed instance.
        dynamic d = vtObj;
        int result = await (Task<int>)d.AsTask();
        return result;
    }

    private sealed class FailingFactory : IRedisConnectionFactory
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return ValueTask.FromCanceled<Result<IRedisConnection>>(ct);

            // LanguageExt.Common.Result<T> does not expose a static Fail(...) helper.
            // Use the failure constructor directly.
            return ValueTask.FromResult(
                new Result<IRedisConnection>(
                    new InvalidOperationException("No connection in unit test.")));
        }

    }
}
