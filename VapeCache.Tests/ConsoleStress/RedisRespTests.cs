using System.Net.Sockets;
using LanguageExt;
using LanguageExt.Common;
using VapeCache.Abstractions.Connections;
using VapeCache.Console.Stress;

namespace VapeCache.Tests.ConsoleStress;

public sealed class RedisRespTests
{
    [Fact]
    public void BuildCommand_formats_resp_array()
    {
        var cmd = RedisResp.BuildCommand("SET", "k1", "v1");
        var text = System.Text.Encoding.UTF8.GetString(cmd);

        Assert.Equal("*3\r\n$3\r\nSET\r\n$2\r\nk1\r\n$2\r\nv1\r\n", text);
    }

    [Fact]
    public async Task ReadLineAsync_reads_until_crlf()
    {
        await using var conn = new StubConnection("+PONG\r\n"u8.ToArray());

        var line = await RedisResp.ReadLineAsync(conn, CancellationToken.None);

        Assert.Equal("+PONG", line);
    }

    private sealed class StubConnection(byte[] response) : IRedisConnection
    {
        private int _offset;

        public Socket Socket { get; } = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public Stream Stream { get; } = Stream.Null;

        public ValueTask<Result<Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            => ValueTask.FromResult<Result<Unit>>(Prelude.unit);

        public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_offset >= response.Length)
                return ValueTask.FromResult<Result<int>>(0);

            buffer.Span[0] = response[_offset++];
            return ValueTask.FromResult<Result<int>>(1);
        }

        public ValueTask DisposeAsync()
        {
            try { Socket.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
