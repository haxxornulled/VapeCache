using BenchmarkDotNet.Attributes;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class RedisRespReaderBenchmarks
{
    private MemoryStream _simpleString = null!;
    private MemoryStream _bulkString = null!;
    private MemoryStream _arrayOfBulks = null!;

    [GlobalSetup]
    public void Setup()
    {
        _simpleString = new MemoryStream("+PONG\r\n"u8.ToArray(), writable: false);
        _bulkString = new MemoryStream("$5\r\nhello\r\n"u8.ToArray(), writable: false);
        _arrayOfBulks = new MemoryStream("*3\r\n$1\r\na\r\n$2\r\nbb\r\n$3\r\nccc\r\n"u8.ToArray(), writable: false);
    }

    [Benchmark]
    public async ValueTask ReadSimpleString()
    {
        _simpleString.Position = 0;
        _ = await RedisRespReader.ReadAsync(_simpleString, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask ReadBulkString()
    {
        _bulkString.Position = 0;
        _ = await RedisRespReader.ReadAsync(_bulkString, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    public async ValueTask ReadArrayOfBulks()
    {
        _arrayOfBulks.Position = 0;
        _ = await RedisRespReader.ReadAsync(_arrayOfBulks, CancellationToken.None).ConfigureAwait(false);
    }
}

