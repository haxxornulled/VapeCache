using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class RedisMultiplexedBufferCachesTests
{
    [Fact]
    public void RentHeaderBuffer_ReturnsAtLeastRequestedLength_WhenTlsCacheHasSmallerBuffer()
    {
        var first = RedisMultiplexedBufferCaches.RentHeaderBuffer(600);
        RedisMultiplexedBufferCaches.ReturnHeaderBuffer(first);

        var second = RedisMultiplexedBufferCaches.RentHeaderBuffer(5000);
        Assert.True(second.Length >= 5000, $"Expected header buffer length >= 5000, got {second.Length}");
    }

    [Fact]
    public void RentPayloadArray_ReturnsAtLeastRequestedLength_WhenTlsCacheHasSmallerArray()
    {
        var first = RedisMultiplexedBufferCaches.RentPayloadArray(32);
        RedisMultiplexedBufferCaches.ReturnPayloadArray(first);

        var second = RedisMultiplexedBufferCaches.RentPayloadArray(128);
        Assert.True(second.Length >= 128, $"Expected payload array length >= 128, got {second.Length}");
    }
}
