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

    [Fact]
    public async Task ReadAsync_SimpleString_SplitAcrossReads_ParsesPayload()
    {
        await using var stream = new ChunkedReadStream(
            "+PO"u8.ToArray(),
            "NG\r\n"u8.ToArray());
        await using var state = new RedisRespReaderState(stream, bufferSize: 4);

        var response = await state.ReadAsync(CancellationToken.None);
        Assert.Equal(RedisRespReader.RespKind.SimpleString, response.Kind);
        Assert.Equal("PONG", response.Text);
    }

    [Fact]
    public async Task ReadCountAsync_ZRangeWithScoresCount_ParsesBufferedBulkScore()
    {
        await using var state = new RedisRespReaderState(
            new MemoryStream(Encoding.UTF8.GetBytes("*2\r\n$3\r\nfoo\r\n$3\r\n1.5\r\n")));

        var response = await state.ReadCountAsync(RedisResponseMode.ZRangeWithScoresCount, CancellationToken.None);
        Assert.Equal(RedisRespReader.RespKind.Integer, response.Kind);
        Assert.Equal(1, response.IntegerValue);
    }

    [Fact]
    public async Task ReadCountAsync_FtSearchCount_ParsesSplitBulkInteger()
    {
        await using var stream = new ChunkedReadStream(
            "*1\r\n$5\r\n12"u8.ToArray(),
            "345\r\n"u8.ToArray());
        await using var state = new RedisRespReaderState(stream, bufferSize: 8);

        var response = await state.ReadCountAsync(RedisResponseMode.FtSearchCount, CancellationToken.None);
        Assert.Equal(RedisRespReader.RespKind.Integer, response.Kind);
        Assert.Equal(12345, response.IntegerValue);
    }

    [Fact]
    public async Task ReadCountAsync_BulkStringDiscard_ReturnsEmptyBulkString()
    {
        await using var state = new RedisRespReaderState(
            new MemoryStream(Encoding.UTF8.GetBytes("$5\r\nhello\r\n")));

        var response = await state.ReadCountAsync(RedisResponseMode.BulkStringDiscard, CancellationToken.None);
        Assert.Equal(RedisRespReader.RespKind.BulkString, response.Kind);
        Assert.Equal(0, response.BulkLength);
        Assert.NotNull(response.Bulk);
        Assert.Empty(response.Bulk);
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _chunkIndex;
        private int _chunkOffset;

        public ChunkedReadStream(params byte[][] chunks)
        {
            _chunks = chunks;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (_chunkIndex < _chunks.Length)
            {
                var chunk = _chunks[_chunkIndex];
                var remaining = chunk.Length - _chunkOffset;
                if (remaining <= 0)
                {
                    _chunkIndex++;
                    _chunkOffset = 0;
                    continue;
                }

                var toCopy = Math.Min(remaining, buffer.Length);
                chunk.AsSpan(_chunkOffset, toCopy).CopyTo(buffer.Span);
                _chunkOffset += toCopy;
                if (_chunkOffset >= chunk.Length)
                {
                    _chunkIndex++;
                    _chunkOffset = 0;
                }

                return ValueTask.FromResult(toCopy);
            }

            return ValueTask.FromResult(0);
        }
    }
}
