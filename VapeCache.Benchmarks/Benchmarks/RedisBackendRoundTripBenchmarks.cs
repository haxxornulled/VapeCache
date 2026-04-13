using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

[BenchmarkCategory("Live")]
public class RedisBackendRoundTripBenchmarks : IAsyncDisposable
{
    private RedisCommandExecutor? _executor;
    private byte[] _payload = Array.Empty<byte>();
    private string _key = string.Empty;

    public IEnumerable<string> BackendValues => ResolveBackends();
    public IEnumerable<int> PayloadByteValues => ResolvePayloadBytes();

    [ParamsSource(nameof(BackendValues))]
    public string Backend { get; set; } = "redis";

    [ParamsSource(nameof(PayloadByteValues))]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        Random.Shared.NextBytes(_payload);
        _key = $"bench:live:{Backend}:{Guid.NewGuid():N}";

        var endpoint = ResolveEndpoint(Backend);

        var connectionOptions = new RedisConnectionOptions
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            AcquireTimeout = TimeSpan.FromSeconds(2),
            MaxConnections = 16,
            MaxIdle = 16,
            Warm = 2,
            RespProtocolVersion = 2,
            TransportProfile = RedisTransportProfile.Balanced
        };

        var multiplexerOptions = new RedisMultiplexerOptions
        {
            Connections = 2,
            MaxInFlightPerConnection = 1024,
            EnableCommandInstrumentation = false,
            EnableCoalescedSocketWrites = true,
            EnableAdaptiveCoalescing = true,
            TransportProfile = RedisTransportProfile.Balanced
        };

        var factory = new RedisConnectionFactory(
            new BenchmarkOptionsMonitor<RedisConnectionOptions>(connectionOptions),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        _executor = new RedisCommandExecutor(factory, Options.Create(multiplexerOptions));

        _ = await _executor.SetAsync(_key, _payload, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_executor is null)
            return;

        try
        {
            _ = await _executor.DeleteAsync(_key, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        await _executor.DisposeAsync().ConfigureAwait(false);
        _executor = null;
    }

    [Benchmark(Baseline = true)]
    public Task<string> Ping()
        => _executor!.PingAsync(CancellationToken.None).AsTask();

    [Benchmark]
    public Task<bool> Set()
        => _executor!.SetAsync(_key, _payload, TimeSpan.FromMinutes(2), CancellationToken.None).AsTask();

    [Benchmark]
    public Task<byte[]?> Get()
        => _executor!.GetAsync(_key, CancellationToken.None).AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_executor is not null)
            await CleanupAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    private static (string Host, int Port) ResolveEndpoint(string backend)
        => backend.ToLowerInvariant() switch
        {
            "redis" => (
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_REDIS_HOST") ?? "127.0.0.1",
                ParsePort(Environment.GetEnvironmentVariable("VAPECACHE_BENCH_REDIS_PORT"), 6379)),
            "keydb" => (
                Environment.GetEnvironmentVariable("VAPECACHE_BENCH_KEYDB_HOST") ?? "127.0.0.1",
                ParsePort(Environment.GetEnvironmentVariable("VAPECACHE_BENCH_KEYDB_PORT"), 6380)),
            _ => throw new InvalidOperationException($"Unsupported benchmark backend '{backend}'.")
        };

    private static int ParsePort(string? raw, int fallback)
        => int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;

    private static IEnumerable<string> ResolveBackends()
    {
        var raw = Environment.GetEnvironmentVariable("VAPECACHE_BENCH_BACKENDS");
        if (string.IsNullOrWhiteSpace(raw))
            return ["redis", "keydb"];

        var values = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant())
            .Where(static value => value is "redis" or "keydb")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return values.Length == 0 ? ["redis", "keydb"] : values;
    }

    private static IEnumerable<int> ResolvePayloadBytes()
    {
        var raw = Environment.GetEnvironmentVariable("VAPECACHE_BENCH_PAYLOADS");
        if (string.IsNullOrWhiteSpace(raw))
            return [256, 1024];

        var values = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Where(static value => value > 0)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();

        return values.Length == 0 ? [256, 1024] : values;
    }
}
