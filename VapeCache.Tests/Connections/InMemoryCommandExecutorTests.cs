using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class InMemoryCommandExecutorTests
{
    [Fact]
    public async Task ListOperations_AreThreadSafeUnderConcurrentPush()
    {
        var executor = new InMemoryCommandExecutor();
        const int workers = 8;
        const int perWorker = 200;
        var key = "list:concurrency";

        var tasks = Enumerable.Range(0, workers)
            .Select(async w =>
            {
                for (var i = 0; i < perWorker; i++)
                    await executor.LPushAsync(key, new byte[] { (byte)w, (byte)i }, default);
            });

        await Task.WhenAll(tasks);

        var len = await executor.LLenAsync(key, default);
        Assert.Equal(workers * perWorker, len);
    }

    [Fact]
    public async Task SetOperations_AreThreadSafeUnderConcurrentAdd()
    {
        var executor = new InMemoryCommandExecutor();
        const int workers = 8;
        const int perWorker = 200;
        var key = "set:concurrency";

        var tasks = Enumerable.Range(0, workers)
            .Select(async w =>
            {
                for (var i = 0; i < perWorker; i++)
                    await executor.SAddAsync(key, new byte[] { (byte)w, (byte)i }, default);
            });

        await Task.WhenAll(tasks);

        var count = await executor.SCardAsync(key, default);
        Assert.Equal(workers * perWorker, count);
    }

    [Fact]
    public async Task HashSet_ReturnsCorrectNewFieldFlag()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "hash:flags";

        var first = await executor.HSetAsync(key, "field", new byte[] { 1 }, default);
        var second = await executor.HSetAsync(key, "field", new byte[] { 2 }, default);

        Assert.Equal(1L, first);
        Assert.Equal(0L, second);
    }

    [Fact]
    public async Task SortedSetOperations_ReturnOrderedMembersAndRanks()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "zset:demo";

        await executor.ZAddAsync(key, 2, new byte[] { 1 }, default);
        await executor.ZAddAsync(key, 1, new byte[] { 2 }, default);

        var range = await executor.ZRangeWithScoresAsync(key, 0, -1, false, default);
        Assert.Equal(2, range.Length);
        Assert.Equal(new byte[] { 2 }, range[0].Member);
        Assert.Equal(1d, range[0].Score);
        Assert.Equal(new byte[] { 1 }, range[1].Member);
        Assert.Equal(2d, range[1].Score);

        var rank = await executor.ZRankAsync(key, new byte[] { 2 }, false, default);
        Assert.Equal(0, rank);

        await executor.ZAddAsync(key, 0, new byte[] { 1 }, default);
        var updatedRange = await executor.ZRangeWithScoresAsync(key, 0, -1, false, default);
        Assert.Equal(new byte[] { 1 }, updatedRange[0].Member);
        Assert.Equal(0d, updatedRange[0].Score);
    }

    [Fact]
    public async Task SortedSetRangeByScore_RespectsOffsetAndCount()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "zset:range";

        await executor.ZAddAsync(key, 1, new byte[] { 1 }, default);
        await executor.ZAddAsync(key, 2, new byte[] { 2 }, default);
        await executor.ZAddAsync(key, 3, new byte[] { 3 }, default);

        var result = await executor.ZRangeByScoreWithScoresAsync(key, 1, 3, false, 1, 1, default);
        Assert.Single(result);
        Assert.Equal(2d, result[0].Score);
    }

    [Fact]
    public async Task ScanFiltersKeysByPattern()
    {
        var executor = new InMemoryCommandExecutor();
        await executor.SetAsync("user:1", new byte[] { 1 }, null, default);
        await executor.SetAsync("session:1", new byte[] { 2 }, null, default);

        var keys = new List<string>();
        await foreach (var key in executor.ScanAsync("user:*", 10, default))
            keys.Add(key);

        Assert.Contains("user:1", keys);
        Assert.DoesNotContain("session:1", keys);
    }

    [Fact]
    public async Task BloomFilter_AddAndExists()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "bf:demo";
        var member = new byte[] { 9, 9 };

        var added = await executor.BfAddAsync(key, member, default);
        Assert.True(added);

        var exists = await executor.BfExistsAsync(key, member, default);
        Assert.True(exists);
    }

    [Fact]
    public async Task TimeSeries_AddAndRange()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "ts:demo";

        await executor.TsCreateAsync(key, default);
        await executor.TsAddAsync(key, 1, 10.5, default);
        await executor.TsAddAsync(key, 2, 20.25, default);

        var range = await executor.TsRangeAsync(key, 1, 2, default);
        Assert.Equal(2, range.Length);
        Assert.Equal(10.5, range[0].Value, 3);
        Assert.Equal(20.25, range[1].Value, 3);
    }

    [Fact]
    public async Task TimeSeries_Add_NaN_IsAccepted()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "ts:nan";

        await executor.TsCreateAsync(key, default);
        await executor.TsAddAsync(key, 1, double.NaN, default);

        var range = await executor.TsRangeAsync(key, 1, 1, default);
        Assert.Single(range);
        Assert.True(double.IsNaN(range[0].Value));
    }

    [Fact]
    public async Task Streams_IdempotentXAdd_ReturnsSameEntryId_ForSameProducerAndIdempotentId()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "stream:orders";
        var fields = new[] { ("orderId", (ReadOnlyMemory<byte>)"123"u8.ToArray()) };

        var first = await executor.XAddIdempotentAsync(
            key,
            producerId: "producer-a",
            idempotentId: "tx-123",
            useAutoIdempotentId: false,
            entryId: "*",
            fields,
            default);

        var second = await executor.XAddIdempotentAsync(
            key,
            producerId: "producer-a",
            idempotentId: "tx-123",
            useAutoIdempotentId: false,
            entryId: "*",
            fields,
            default);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task JsonGetLease_ReturnsStoredBytes()
    {
        var executor = new InMemoryCommandExecutor();
        var key = "json:lease";
        var payload = new byte[] { 1, 2, 3 };

        await executor.JsonSetAsync(key, ".", payload, default);

        var lease = await executor.JsonGetLeaseAsync(key, ".", default);
        Assert.False(lease.IsNull);
        Assert.Equal(payload, lease.Memory.ToArray());
        lease.Dispose();
    }

    [Fact]
    public async Task JsonSetLease_UsesLeasedPayload()
    {
        var executor = new InMemoryCommandExecutor();
        var sourceKey = "json:lease:source";
        var targetKey = "json:lease:target";
        var payload = new byte[] { 9, 8, 7 };

        await executor.SetAsync(sourceKey, payload, null, default);
        var lease = await executor.GetLeaseAsync(sourceKey, default);

        await executor.JsonSetLeaseAsync(targetKey, ".", lease, default);
        lease.Dispose();

        var stored = await executor.JsonGetAsync(targetKey, ".", default);
        Assert.Equal(payload, stored);
    }

    [Fact]
    public async Task NonDestructiveReads_ReturnCopies_OfStoredBuffers()
    {
        var executor = new InMemoryCommandExecutor();

        var stringValue = new byte[] { 1, 2, 3 };
        await executor.SetAsync("copy:string", stringValue, null, default);
        stringValue[0] = 99;

        var firstStringRead = await executor.GetAsync("copy:string", default);
        Assert.Equal(new byte[] { 1, 2, 3 }, firstStringRead);

        firstStringRead![1] = 77;
        var secondStringRead = await executor.GetAsync("copy:string", default);
        Assert.Equal(new byte[] { 1, 2, 3 }, secondStringRead);

        var hashValue = new byte[] { 4, 5, 6 };
        await executor.HSetAsync("copy:hash", "field", hashValue, default);
        hashValue[0] = 55;

        var firstHashRead = await executor.HGetAsync("copy:hash", "field", default);
        Assert.Equal(new byte[] { 4, 5, 6 }, firstHashRead);

        firstHashRead![2] = 88;
        var secondHashRead = await executor.HGetAsync("copy:hash", "field", default);
        Assert.Equal(new byte[] { 4, 5, 6 }, secondHashRead);

        await executor.SAddAsync("copy:set", new byte[] { 7, 8 }, default);
        var firstMembers = await executor.SMembersAsync("copy:set", default);
        Assert.Single(firstMembers);
        firstMembers[0]![0] = 42;

        var secondMembers = await executor.SMembersAsync("copy:set", default);
        Assert.Equal(new byte[] { 7, 8 }, secondMembers[0]);
    }
}
