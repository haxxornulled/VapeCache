using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class RedisRespReaderStateTests
{
    [Fact]
    public async Task ReadAsync_SimpleOkString_UsesCanonicalInstance()
    {
        await using var state = new RedisRespReaderState(
            new MemoryStream(Encoding.UTF8.GetBytes("+OK\r\n")));

        var response = await state.ReadAsync(CancellationToken.None);
        Assert.Equal(RedisRespReader.RespKind.SimpleString, response.Kind);
        Assert.NotNull(response.Text);
        Assert.True(ReferenceEquals(response.Text, RedisRespReader.OkSimpleString));
    }

    [Fact]
    public async Task ReadAsync_SimpleString_ParsesNonOkPayload()
    {
        await using var state = new RedisRespReaderState(
            new MemoryStream(Encoding.UTF8.GetBytes("+PONG\r\n")));

        var response = await state.ReadAsync(CancellationToken.None);
        Assert.Equal(RedisRespReader.RespKind.SimpleString, response.Kind);
        Assert.Equal("PONG", response.Text);
    }
}
