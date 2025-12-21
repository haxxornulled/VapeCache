using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using LanguageExt.Common;
using VapeCache.Application.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class RedisMultiplexedConnectionBenchmarks
{
    private RedisMultiplexedConnection? _mux;
    private ReadOnlyMemory<byte> _cmd;

    [Params(8, 64)]
    public int MaxInFlight { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cmd = RedisRespProtocol.PingCommand;
        _mux = new RedisMultiplexedConnection(new FakeFactory(), MaxInFlight);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_mux is not null)
            await _mux.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask Execute_PingPong()
    {
        _ = await _mux!.ExecuteAsync(_cmd, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class FakeFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new FakeConn(new LoopbackRespStream())));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeConn(Stream stream) : IRedisConnection
        {
            public System.Net.Sockets.Socket Socket => throw new NotSupportedException();
            public Stream Stream => stream;

            public ValueTask<Result<LanguageExt.Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
                => ValueTask.FromResult<Result<LanguageExt.Unit>>(LanguageExt.Prelude.unit);

            public ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
                => ValueTask.FromResult<Result<int>>(0);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class LoopbackRespStream : Stream
        {
            private static readonly byte[] Pong = "+PONG\r\n"u8.ToArray();
            private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });

            private byte[]? _current;
            private int _offset;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // One response per request; RespReader reads exactly one response per ExecuteAsync.
                _responses.Writer.TryWrite(Pong);
                return ValueTask.CompletedTask;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    if (_current is null || _offset >= _current.Length)
                    {
                        _current = await _responses.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        _offset = 0;
                    }

                    var remaining = _current.Length - _offset;
                    var toCopy = Math.Min(remaining, buffer.Length);
                    _current.AsSpan(_offset, toCopy).CopyTo(buffer.Span);
                    _offset += toCopy;
                    return toCopy;
                }
            }
        }
    }
}

