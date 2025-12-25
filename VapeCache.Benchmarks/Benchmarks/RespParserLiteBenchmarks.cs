using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
public class RespParserLiteBenchmarks
{
    public enum FrameKind
    {
        Ok,
        Integer,
        Error,
        BulkSmall,
        Bulk4K,
        ArrayGet
    }

    [Params(FrameKind.Ok, FrameKind.Integer, FrameKind.Error, FrameKind.BulkSmall, FrameKind.Bulk4K, FrameKind.ArrayGet)]
    public FrameKind Frame { get; set; }

    private ReadOnlyMemory<byte> _buffer;
    private readonly Consumer _consumer = new();

    [GlobalSetup]
    public void Setup()
    {
        _buffer = Frame switch
        {
            FrameKind.Ok => "+OK\r\n"u8.ToArray(),
            FrameKind.Integer => ":123456789\r\n"u8.ToArray(),
            FrameKind.Error => "-ERR msg\r\n"u8.ToArray(),
            FrameKind.BulkSmall => "$3\r\nfoo\r\n"u8.ToArray(),
            FrameKind.Bulk4K => BuildBulkStringBytes(4096),
            FrameKind.ArrayGet => "*2\r\n$3\r\nGET\r\n$32\r\n0123456789abcdef0123456789abcdef\r\n"u8.ToArray(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    [Benchmark]
    public void TryParse()
    {
        RespParserLite.TryParse(_buffer, out var consumed, out _);
        _consumer.Consume(consumed);
    }

    private static byte[] BuildBulkStringBytes(int payloadLength)
        => System.Text.Encoding.ASCII.GetBytes($"${payloadLength}\r\n{new string('x', payloadLength)}\r\n");
}
