using BenchmarkDotNet.Attributes;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class RedisConnectionStringParserBenchmarks
{
    private string _connStr = "";

    [GlobalSetup]
    public void Setup()
    {
        _connStr = "redis://user:pass@127.0.0.1:6379/0";
    }

    [Benchmark]
    public bool TryParse()
        => RedisConnectionStringParser.TryParse(_connStr, out _, out _);
}
