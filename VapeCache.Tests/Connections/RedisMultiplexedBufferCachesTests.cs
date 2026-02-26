using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class RedisMultiplexedBufferCachesTests
{
    [Fact]
    public void RentHeaderBuffer_ReturnsAtLeastRequestedLength_WhenTlsCacheHasSmallerBuffer()
    {
        var caches = new RedisMultiplexedBufferCaches();

        var first = caches.RentHeaderBuffer(600);
        caches.ReturnHeaderBuffer(first);

        var second = caches.RentHeaderBuffer(5000);
        Assert.True(second.Length >= 5000, $"Expected header buffer length >= 5000, got {second.Length}");
    }

    [Fact]
    public void RentPayloadArray_ReturnsAtLeastRequestedLength_WhenTlsCacheHasSmallerArray()
    {
        var caches = new RedisMultiplexedBufferCaches();

        var first = caches.RentPayloadArray(32);
        caches.ReturnPayloadArray(first);

        var second = caches.RentPayloadArray(128);
        Assert.True(second.Length >= 128, $"Expected payload array length >= 128, got {second.Length}");
    }
}

