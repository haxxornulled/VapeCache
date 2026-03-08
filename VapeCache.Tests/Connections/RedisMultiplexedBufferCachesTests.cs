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

    [Fact]
    public void CallerReturn_IsIgnored_WhileHeaderBufferOwnedByMux()
    {
        var header = RedisMultiplexedBufferCaches.RentHeaderBuffer(4096);
        RedisMultiplexedBufferCaches.MarkHeaderBufferInFlight(header);

        RedisMultiplexedBufferCaches.ReturnHeaderBufferFromCaller(header);

        var other = RedisMultiplexedBufferCaches.RentHeaderBuffer(4096);
        Assert.False(ReferenceEquals(header, other));
        RedisMultiplexedBufferCaches.ReturnHeaderBufferFromCaller(other);

        RedisMultiplexedBufferCaches.ReturnHeaderBufferFromMux(header);

        var rerented = RedisMultiplexedBufferCaches.RentHeaderBuffer(4096);
        Assert.True(ReferenceEquals(header, rerented));
        RedisMultiplexedBufferCaches.ReturnHeaderBufferFromCaller(rerented);
    }

    [Fact]
    public void CallerReturn_IsIgnored_WhilePayloadArrayOwnedByMux()
    {
        var payloads = RedisMultiplexedBufferCaches.RentPayloadArray(96);
        RedisMultiplexedBufferCaches.MarkPayloadArrayInFlight(payloads);

        RedisMultiplexedBufferCaches.ReturnPayloadArrayFromCaller(payloads);

        var other = RedisMultiplexedBufferCaches.RentPayloadArray(96);
        Assert.False(ReferenceEquals(payloads, other));
        RedisMultiplexedBufferCaches.ReturnPayloadArrayFromCaller(other);

        RedisMultiplexedBufferCaches.ReturnPayloadArrayFromMux(payloads);

        var rerented = RedisMultiplexedBufferCaches.RentPayloadArray(96);
        Assert.True(rerented.Length >= 96);
        RedisMultiplexedBufferCaches.ReturnPayloadArrayFromCaller(rerented);
    }
}
