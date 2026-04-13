using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[BenchmarkCategory("Micro")]
[MemoryDiagnoser]
public class RespParserLiteBenchmarks
{
    private readonly Consumer _consumer = new();
    private ReadOnlyMemory<byte> _frame;

    public enum FrameKind
    {
        SimpleString,
        IntegerFrame,
        BulkString,
        ArrayHeader
    }

    [ParamsAllValues]
    public FrameKind Kind { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frame = Kind switch
        {
            FrameKind.SimpleString => "+PONG\r\n"u8.ToArray(),
            FrameKind.IntegerFrame => ":123456\r\n"u8.ToArray(),
            FrameKind.BulkString => "$11\r\nhello world\r\n"u8.ToArray(),
            FrameKind.ArrayHeader => "*3\r\n"u8.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }

    [Benchmark]
    public bool TryParse()
    {
        var parsed = RespParserLite.TryParse(_frame, out var consumed, out var value);

        _consumer.Consume(consumed);
        _consumer.Consume((int)value.Kind);
        _consumer.Consume(value.Integer);
        _consumer.Consume(value.ArrayLength);

        if (!value.Data.IsEmpty)
            _consumer.Consume(value.Data.Length);

        return parsed;
    }
}
