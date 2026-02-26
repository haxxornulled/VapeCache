using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks;

/// <summary>
/// Standalone performance comparison tool that doesn't rely on BenchmarkDotNet.
/// Provides quick, direct measurements of VapeCache vs StackExchange.Redis.
/// </summary>
public class StandalonePerformanceTest
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;

    public StandalonePerformanceTest(string host, int port = 6379, string? username = null, string? password = null)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
    }

    public async Task RunAsync()
    {
        Console.Out.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.Out.WriteLine("VapeCache vs StackExchange.Redis - Standalone Performance Test");
        Console.Out.WriteLine("═══════════════════════════════════════════════════════════════\n");

        // Test configurations
        var payloadSizes = new[] { 32, 256, 1024, 4096 };
        var iterations = 10000;

        foreach (var payloadSize in payloadSizes)
        {
            Console.Out.WriteLine($"\n━━━ Payload Size: {payloadSize} bytes ━━━\n");
            await RunComparisonAsync(payloadSize, iterations);
        }

        Console.Out.WriteLine("\n═══════════════════════════════════════════════════════════════");
        Console.Out.WriteLine("Test Complete!");
        Console.Out.WriteLine("═══════════════════════════════════════════════════════════════");
    }

    private async Task RunComparisonAsync(int payloadSize, int iterations)
    {
        var payload = new byte[payloadSize];
        new Random(42).NextBytes(payload);
        var key = $"perf:test:{Guid.NewGuid():N}";

        // Connect both clients
        Console.Out.WriteLine("Connecting clients...");
        var (serMux, serDb) = await ConnectStackExchangeAsync();
        var vapeCache = await ConnectVapeCacheAsync();

        try
        {
            // Warmup
            Console.Out.WriteLine("Warming up...");
            await serDb.StringSetAsync(key, payload);
            await serDb.StringGetAsync(key);
            await vapeCache.SetAsync(key, payload, ttl: null, CancellationToken.None);
            await vapeCache.GetAsync(key, CancellationToken.None);
            await Task.Delay(100);

            // Benchmark SET operations
            Console.Out.WriteLine($"\nSET operations ({iterations:N0} iterations):");
            var seSetMs = await BenchmarkStackExchangeSetAsync(serDb, key, payload, iterations);
            var vcSetMs = await BenchmarkVapeCacheSetAsync(vapeCache, key, payload, iterations);

            Console.Out.WriteLine($"  StackExchange.Redis: {seSetMs:N2} ms ({iterations / seSetMs * 1000:N0} ops/sec)");
            Console.Out.WriteLine($"  VapeCache:           {vcSetMs:N2} ms ({iterations / vcSetMs * 1000:N0} ops/sec)");
            Console.Out.WriteLine($"  Speedup:             {seSetMs / vcSetMs:F2}x faster ({(1 - vcSetMs / seSetMs) * 100:F1}% improvement)");

            // Benchmark GET operations
            Console.Out.WriteLine($"\nGET operations ({iterations:N0} iterations):");
            var seGetMs = await BenchmarkStackExchangeGetAsync(serDb, key, iterations);
            var vcGetMs = await BenchmarkVapeCacheGetAsync(vapeCache, key, iterations);

            Console.Out.WriteLine($"  StackExchange.Redis: {seGetMs:N2} ms ({iterations / seGetMs * 1000:N0} ops/sec)");
            Console.Out.WriteLine($"  VapeCache:           {vcGetMs:N2} ms ({iterations / vcGetMs * 1000:N0} ops/sec)");
            Console.Out.WriteLine($"  Speedup:             {seGetMs / vcGetMs:F2}x faster ({(1 - vcGetMs / seGetMs) * 100:F1}% improvement)");

            // Cleanup
            await serDb.KeyDeleteAsync(key);
        }
        finally
        {
            serMux?.Dispose();
            if (vapeCache != null) await vapeCache.DisposeAsync();
        }
    }

    private async Task<double> BenchmarkStackExchangeSetAsync(IDatabase db, string key, byte[] payload, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await db.StringSetAsync(key, payload);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private async Task<double> BenchmarkStackExchangeGetAsync(IDatabase db, string key, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var _ = await db.StringGetAsync(key);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private async Task<double> BenchmarkVapeCacheSetAsync(RedisCommandExecutor executor, string key, byte[] payload, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            await executor.SetAsync(key, payload, ttl: null, CancellationToken.None);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private async Task<double> BenchmarkVapeCacheGetAsync(RedisCommandExecutor executor, string key, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var _ = await executor.GetAsync(key, CancellationToken.None);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private async Task<(IConnectionMultiplexer Mux, IDatabase Db)> ConnectStackExchangeAsync()
    {
        var config = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectRetry = 5,
            ConnectTimeout = 5000,
            SyncTimeout = 15000,
            AsyncTimeout = 15000,
            User = _username,
            Password = _password
        };
        config.EndPoints.Add(_host, _port);

        var mux = await ConnectionMultiplexer.ConnectAsync(config);
        await mux.GetDatabase().PingAsync(); // Verify connection
        return (mux, mux.GetDatabase());
    }

    private async Task<RedisCommandExecutor> ConnectVapeCacheAsync()
    {
        var options = new RedisConnectionOptions
        {
            Host = _host,
            Port = _port,
            Username = _username,
            Password = _password,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };

        var monitor = new OptionsMonitorStub<RedisConnectionOptions>(options);
        var factory = new RedisConnectionFactory(monitor, NullLogger<RedisConnectionFactory>.Instance, Array.Empty<IRedisConnectionObserver>());

        var executor = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCommandInstrumentation = false,
                EnableCoalescedSocketWrites = true
            }));

        // Verify connection with a simple GET
        try
        {
            await executor.GetAsync("__vapecache_connection_test__", CancellationToken.None);
        }
        catch
        {
            // Key doesn't exist is fine - we just want to verify connectivity
        }

        return executor;
    }
}

// Helper class for IOptionsMonitor
file class OptionsMonitorStub<T> : IOptionsMonitor<T>
{
    private readonly T _value;
    public OptionsMonitorStub(T value) => _value = value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

