using BenchmarkDotNet.Attributes;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[BenchmarkCategory("Micro")]
[MemoryDiagnoser]
public class RedisRespProtocolBenchmarks
{
    private const int TtlMilliseconds = 30_000;

    private string _key = string.Empty;
    private byte[] _payload = Array.Empty<byte>();
    private byte[] _getBuffer = Array.Empty<byte>();
    private byte[] _getExBuffer = Array.Empty<byte>();
    private byte[] _setBuffer = Array.Empty<byte>();

    [Params(16, 64)]
    public int KeyLength { get; set; }

    [Params(64, 1024)]
    public int PayloadBytes { get; set; }

    [Params(false, true)]
    public bool WithTtl { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _key = new string('k', KeyLength);
        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        Random.Shared.NextBytes(_payload);

        _getBuffer = GC.AllocateUninitializedArray<byte>(RedisRespProtocol.GetGetCommandLength(_key));
        _getExBuffer = GC.AllocateUninitializedArray<byte>(RedisRespProtocol.GetGetExCommandLength(_key, ResolveTtl()));
        _setBuffer = GC.AllocateUninitializedArray<byte>(RedisRespProtocol.GetSetCommandLength(_key, _payload.Length, ResolveTtl()));
    }

    [Benchmark(Baseline = true)]
    public int GetGetLength()
        => RedisRespProtocol.GetGetCommandLength(_key);

    [Benchmark]
    public int WriteGet()
        => RedisRespProtocol.WriteGetCommand(_getBuffer, _key);

    [Benchmark]
    public int GetGetExLength()
        => RedisRespProtocol.GetGetExCommandLength(_key, ResolveTtl());

    [Benchmark]
    public int WriteGetEx()
        => RedisRespProtocol.WriteGetExCommand(_getExBuffer, _key, ResolveTtl());

    [Benchmark]
    public int GetSetLength()
        => RedisRespProtocol.GetSetCommandLength(_key, _payload.Length, ResolveTtl());

    [Benchmark]
    public int WriteSet()
        => RedisRespProtocol.WriteSetCommand(_setBuffer, _key, _payload, ResolveTtl());

    private int? ResolveTtl() => WithTtl ? TtlMilliseconds : null;
}
