using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using VapeCache.Benchmarks;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
public class RedisRespProtocolWriteGetBenchmarks
{
    [Params(8, 32)]
    public int KeyLength { get; set; }

    private string _key = null!;
    private byte[] _buffer = null!;
    private readonly Consumer _consumer = new();

    [GlobalSetup]
    public void Setup()
    {
        _key = new string('k', KeyLength);
        _buffer = GC.AllocateUninitializedArray<byte>(RedisRespProtocol.GetGetCommandLength(_key));
    }

    [Benchmark]
    public void WriteGet()
        => _consumer.Consume(RedisRespProtocol.WriteGetCommand(_buffer, _key));
}

[Config(typeof(EnterpriseBenchmarkConfig))]
public class RedisRespProtocolWriteGetExBenchmarks
{
    [Params(8, 32)]
    public int KeyLength { get; set; }

    [Params(0, 60000)]
    public int TtlMs { get; set; }

    private string _key = null!;
    private byte[] _buffer = null!;
    private readonly Consumer _consumer = new();

    [GlobalSetup]
    public void Setup()
    {
        _key = new string('k', KeyLength);
        _buffer = GC.AllocateUninitializedArray<byte>(RedisRespProtocol.GetGetExCommandLength(_key, TtlMs == 0 ? null : TtlMs));
    }

    [Benchmark]
    public void WriteGetEx()
        => _consumer.Consume(RedisRespProtocol.WriteGetExCommand(_buffer, _key, TtlMs == 0 ? null : TtlMs));
}

[Config(typeof(EnterpriseBenchmarkConfig))]
public class RedisRespProtocolWriteHSetBenchmarks
{
    [Params(8, 32)]
    public int KeyLength { get; set; }

    [Params(16, 256, 4096)]
    public int ValueLength { get; set; }

    private string _key = null!;
    private string _field = null!;
    private byte[] _value = null!;
    private byte[] _buffer = null!;
    private readonly Consumer _consumer = new();

    [GlobalSetup]
    public void Setup()
    {
        _key = new string('k', KeyLength);
        _field = "field";
        _value = new byte[ValueLength];
        new Random(42).NextBytes(_value);

        _buffer = GC.AllocateUninitializedArray<byte>(RedisRespProtocol.GetHSetCommandLength(_key, _field, _value.Length));
    }

    [Benchmark]
    public void WriteHSetHeader()
        => _consumer.Consume(RedisRespProtocol.WriteHSetCommandHeader(_buffer, _key, _field, _value.Length));

    [Benchmark]
    public void WriteHSetHeaderAndValue()
        => _consumer.Consume(RedisRespProtocol.WriteHSetCommand(_buffer, _key, _field, _value));
}
