using System.Buffers;
using BenchmarkDotNet.Attributes;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class RedisRespProtocolBenchmarks
{
    [Params(64, 1024, 4096)]
    public int PayloadBytes { get; set; }

    [Params(false, true)]
    public bool WithTtl { get; set; }

    private byte[] _payload = Array.Empty<byte>();
    private string _key = "";
    private string[] _mgetKeys = Array.Empty<string>();
    private (string Key, ReadOnlyMemory<byte> Value)[] _msetItems = Array.Empty<(string, ReadOnlyMemory<byte>)>();
    private (string Key, int ValueLen)[] _msetLenItems = Array.Empty<(string, int)>();
    private int _msetLen;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadBytes];
        Random.Shared.NextBytes(_payload);
        _key = "bench:key:" + PayloadBytes;

        _mgetKeys = new[] { _key, _key + ":2", _key + ":3", _key + ":4", _key + ":5" };
        _msetItems = new[]
        {
            (_mgetKeys[0], (ReadOnlyMemory<byte>)_payload),
            (_mgetKeys[1], (ReadOnlyMemory<byte>)_payload),
            (_mgetKeys[2], (ReadOnlyMemory<byte>)_payload)
        };
        _msetLenItems = new[]
        {
            (_msetItems[0].Key, _msetItems[0].Value.Length),
            (_msetItems[1].Key, _msetItems[1].Value.Length),
            (_msetItems[2].Key, _msetItems[2].Value.Length)
        };
        _msetLen = RedisRespProtocol.GetMSetCommandLength(_msetLenItems);
    }

    [Benchmark]
    public int WriteSet()
    {
        var ttlMs = WithTtl ? 30_000 : (int?)null;
        var len = RedisRespProtocol.GetSetCommandLength(_key, _payload.Length, ttlMs);
        var rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            return RedisRespProtocol.WriteSetCommand(rented.AsSpan(0, len), _key, _payload, ttlMs);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark]
    public int WriteGet()
    {
        var len = RedisRespProtocol.GetGetCommandLength(_key);
        var rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            return RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), _key);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark]
    public int WriteGetEx()
    {
        var ttlMs = WithTtl ? 30_000 : (int?)null;
        var len = RedisRespProtocol.GetGetExCommandLength(_key, ttlMs);
        var rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            return RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), _key, ttlMs);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark]
    public int WriteMGet_5Keys()
    {
        var len = RedisRespProtocol.GetMGetCommandLength(_mgetKeys);
        var rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            return RedisRespProtocol.WriteMGetCommand(rented.AsSpan(0, len), _mgetKeys);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark]
    public int WriteMSet_3Items()
    {
        var rented = ArrayPool<byte>.Shared.Rent(_msetLen);
        try
        {
            return RedisRespProtocol.WriteMSetCommand(rented.AsSpan(0, _msetLen), _msetItems);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
