using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
public class SocketAwaitableBenchmarks
{
    [Params(0, 1, 123)]
    public int CompletionValue { get; set; }

    private SocketIoAwaitableEventArgs _readerArgs = null!;
    private SocketIoAwaitableEventArgs _muxArgs = null!;
    private ArraySegment<byte>[] _buffers = null!;
    private readonly Consumer _consumer = new();

    [GlobalSetup]
    public void Setup()
    {
        _readerArgs = new SocketIoAwaitableEventArgs();
        _muxArgs = new SocketIoAwaitableEventArgs();
        _buffers = new[] { new ArraySegment<byte>(Array.Empty<byte>()) };
    }

    [Benchmark]
    public void AwaitableSocketArgs_CompleteSync()
    {
        var vt = _readerArgs.BeginForTests();
        _readerArgs.CompleteForTests(CompletionValue);
        var result = vt.GetAwaiter().GetResult();
        _consumer.Consume(result);
    }

    [Benchmark]
    public void SocketAwaitableEventArgs_CompleteSync()
    {
        _muxArgs.Reset();
        _muxArgs.SetBuffer(_buffers, 1);
        var vt = _muxArgs.WaitAsync();
        _muxArgs.CompleteForTests(CompletionValue);
        var result = vt.GetAwaiter().GetResult();
        _consumer.Consume(result);
    }
}
