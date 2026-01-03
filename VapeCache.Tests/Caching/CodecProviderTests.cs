using System.Buffers;
using System.Buffers.Binary;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching.Codecs;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class CodecProviderTests
{
    [Fact]
    public void Register_CustomCodec_IsUsed()
    {
        var provider = new SystemTextJsonCodecProvider();
        var codec = new Int32Codec();

        provider.Register(codec);

        var resolved = provider.Get<int>();
        Assert.Same(codec, resolved);

        var buffer = new ArrayBufferWriter<byte>(4);
        resolved.Serialize(buffer, 42);
        var value = resolved.Deserialize(buffer.WrittenSpan);
        Assert.Equal(42, value);
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
