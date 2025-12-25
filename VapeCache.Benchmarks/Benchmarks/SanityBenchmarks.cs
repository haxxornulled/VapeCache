using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using VapeCache.Benchmarks;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
public class SanityBenchmarks
{
    private readonly Consumer _consumer = new();
    private string _payload = string.Empty;

    [Benchmark(Description = "Sanity: no allocation")]
    public void NoAlloc()
    {
        // Do enough work to avoid "zero measurement" noise.
        var value = 123;
        for (var i = 0; i < 1024; i++)
            value = unchecked((value * 31) + i);
        _consumer.Consume(value);
    }

    [Params(1024, 32768, 1048576)]
    public int BytesToAllocate { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Keep string payload size in the same ballpark as byte[] tests, but deterministic.
        // (chars are 2 bytes each; string object overhead will show up in Allocated.)
        var charCount = Math.Max(1, BytesToAllocate / 2);
        _payload = new string('x', charCount);
    }

    [Benchmark(Description = "Sanity: allocates")]
    public void AllocBytes()
    {
        var bytes = new byte[BytesToAllocate];
        bytes[0] = 1;
        bytes[^1] = 2;
        _consumer.Consume(bytes[0] + bytes[^1]);
    }

    [Benchmark(Description = "Sanity: allocates string")]
    public void AllocString()
    {
        // Force a new managed allocation each invocation (payload itself is prebuilt in GlobalSetup).
        var s = new string(_payload.AsSpan());
        _consumer.Consume(s.Length);
    }
}
