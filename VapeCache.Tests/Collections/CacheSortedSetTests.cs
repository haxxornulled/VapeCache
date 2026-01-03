using System.Buffers;
using System.Buffers.Binary;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Collections;

public sealed class CacheSortedSetTests
{
    [Fact]
    public async Task SortedSet_AddScoreRankAndRange_Work()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSortedSet<int>("scores", executor, codec);

        await set.AddAsync(1, 10);
        await set.AddAsync(2, 5);
        await set.AddAsync(3, 7);

        Assert.Equal(3, await set.CountAsync());
        Assert.Equal(10, await set.ScoreAsync(1));
        Assert.Equal(0, await set.RankAsync(2));
        Assert.Equal(2, await set.RankAsync(1));

        var byRank = await set.RangeByRankAsync(0, -1);
        Assert.Equal(new[] { 2, 3, 1 }, byRank.Select(item => item.Member).ToArray());

        var byScore = await set.RangeByScoreAsync(5, 10);
        Assert.Equal(3, byScore.Length);
    }

    [Fact]
    public async Task SortedSet_StreamAsync_EmitsMembers()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSortedSet<int>("scores", executor, codec);

        await set.AddAsync(1, 10);
        await set.AddAsync(2, 5);

        var seen = new HashSet<int>();
        await foreach (var item in set.StreamAsync())
            seen.Add(item.Member);

        Assert.Equal(2, seen.Count);
        Assert.Contains(1, seen);
        Assert.Contains(2, seen);
    }

    private sealed class Int32Codec : ICacheCodec<int>
    {
        public void Serialize(IBufferWriter<byte> buffer, int value)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(ReadOnlySpan<byte> data)
            => BinaryPrimitives.ReadInt32LittleEndian(data);
    }
}
