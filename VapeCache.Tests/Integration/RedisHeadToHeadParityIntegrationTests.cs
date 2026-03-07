using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using Xunit.Sdk;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RedisHeadToHeadParityIntegrationTests
{
    private static readonly int[] PayloadSizes = [256, 1024, 4096, 16384];
    private const string HashField = "field";
    private const double SortedSetScore = 42.5;

    [SkippableFact]
    public async Task VapeCache_and_StackExchangeRedis_match_core_datatype_matrix()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var executor = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 2,
                MaxInFlightPerConnection = 2048,
                EnableCommandInstrumentation = false
            }));

        using var serMux = await ConnectStackExchangeAsync(options).ConfigureAwait(false);
        var serDb = serMux.GetDatabase(options.Database);
        var prefix = "vapecache:parity:" + Guid.NewGuid().ToString("N");

        foreach (var payloadSize in PayloadSizes)
        {
            var payload = new byte[payloadSize];
            Random.Shared.NextBytes(payload);

            await AssertStringParityAsync(prefix, payloadSize, payload, executor, serDb).ConfigureAwait(false);
            await AssertHashParityAsync(prefix, payloadSize, payload, executor, serDb).ConfigureAwait(false);
            await AssertListParityAsync(prefix, payloadSize, payload, executor, serDb).ConfigureAwait(false);
            await AssertSetParityAsync(prefix, payloadSize, payload, executor, serDb).ConfigureAwait(false);
            await AssertSortedSetParityAsync(prefix, payloadSize, payload, executor, serDb).ConfigureAwait(false);
        }
    }

    private static async Task AssertStringParityAsync(
        string prefix,
        int payloadSize,
        byte[] payload,
        RedisCommandExecutor executor,
        IDatabase serDb)
    {
        var keySer = $"{prefix}:str:ser:{payloadSize}";
        var keyVape = $"{prefix}:str:vape:{payloadSize}";

        try
        {
            Assert.True(await serDb.StringSetAsync(keySer, payload).ConfigureAwait(false));
            Assert.True(await executor.SetAsync(keyVape, payload, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false));

            var ser = (byte[]?)await serDb.StringGetAsync(keySer).ConfigureAwait(false);
            var vape = await executor.GetAsync(keyVape, CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(ser);
            Assert.NotNull(vape);
            Assert.Equal(ser, vape);
        }
        finally
        {
            _ = await serDb.KeyDeleteAsync(new RedisKey[] { keySer, keyVape }).ConfigureAwait(false);
            _ = await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task AssertHashParityAsync(
        string prefix,
        int payloadSize,
        byte[] payload,
        RedisCommandExecutor executor,
        IDatabase serDb)
    {
        var keySer = $"{prefix}:hash:ser:{payloadSize}";
        var keyVape = $"{prefix}:hash:vape:{payloadSize}";

        try
        {
            Assert.True(await serDb.HashSetAsync(keySer, HashField, payload).ConfigureAwait(false));
            Assert.True(await executor.HSetAsync(keyVape, HashField, payload, CancellationToken.None).ConfigureAwait(false) >= 0);

            var ser = (byte[]?)await serDb.HashGetAsync(keySer, HashField).ConfigureAwait(false);
            var vape = await executor.HGetAsync(keyVape, HashField, CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(ser);
            Assert.NotNull(vape);
            Assert.Equal(ser, vape);
        }
        finally
        {
            _ = await serDb.KeyDeleteAsync(new RedisKey[] { keySer, keyVape }).ConfigureAwait(false);
            _ = await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task AssertListParityAsync(
        string prefix,
        int payloadSize,
        byte[] payload,
        RedisCommandExecutor executor,
        IDatabase serDb)
    {
        var keySer = $"{prefix}:list:ser:{payloadSize}";
        var keyVape = $"{prefix}:list:vape:{payloadSize}";

        try
        {
            await serDb.KeyDeleteAsync(keySer).ConfigureAwait(false);
            await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);

            Assert.True(await serDb.ListLeftPushAsync(keySer, payload).ConfigureAwait(false) >= 1);
            Assert.True(await executor.LPushAsync(keyVape, payload, CancellationToken.None).ConfigureAwait(false) >= 1);

            var ser = (byte[]?)await serDb.ListLeftPopAsync(keySer).ConfigureAwait(false);
            var vape = await executor.LPopAsync(keyVape, CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(ser);
            Assert.NotNull(vape);
            Assert.Equal(ser, vape);
        }
        finally
        {
            _ = await serDb.KeyDeleteAsync(new RedisKey[] { keySer, keyVape }).ConfigureAwait(false);
            _ = await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task AssertSetParityAsync(
        string prefix,
        int payloadSize,
        byte[] payload,
        RedisCommandExecutor executor,
        IDatabase serDb)
    {
        var keySer = $"{prefix}:set:ser:{payloadSize}";
        var keyVape = $"{prefix}:set:vape:{payloadSize}";

        try
        {
            await serDb.KeyDeleteAsync(keySer).ConfigureAwait(false);
            await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);

            Assert.True(await serDb.SetAddAsync(keySer, payload).ConfigureAwait(false));
            Assert.True(await executor.SAddAsync(keyVape, payload, CancellationToken.None).ConfigureAwait(false) >= 0);

            var serContains = await serDb.SetContainsAsync(keySer, payload).ConfigureAwait(false);
            var vapeContains = await executor.SIsMemberAsync(keyVape, payload, CancellationToken.None).ConfigureAwait(false);
            var serCard = await serDb.SetLengthAsync(keySer).ConfigureAwait(false);
            var vapeCard = await executor.SCardAsync(keyVape, CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(serContains, vapeContains);
            Assert.Equal((long)serCard, vapeCard);
        }
        finally
        {
            _ = await serDb.KeyDeleteAsync(new RedisKey[] { keySer, keyVape }).ConfigureAwait(false);
            _ = await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task AssertSortedSetParityAsync(
        string prefix,
        int payloadSize,
        byte[] payload,
        RedisCommandExecutor executor,
        IDatabase serDb)
    {
        var keySer = $"{prefix}:zset:ser:{payloadSize}";
        var keyVape = $"{prefix}:zset:vape:{payloadSize}";

        try
        {
            await serDb.KeyDeleteAsync(keySer).ConfigureAwait(false);
            await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);

            Assert.True(await serDb.SortedSetAddAsync(keySer, payload, SortedSetScore).ConfigureAwait(false));
            Assert.True(await executor.ZAddAsync(keyVape, SortedSetScore, payload, CancellationToken.None).ConfigureAwait(false) >= 0);

            var serScore = await serDb.SortedSetScoreAsync(keySer, payload).ConfigureAwait(false);
            var vapeScore = await executor.ZScoreAsync(keyVape, payload, CancellationToken.None).ConfigureAwait(false);
            var serRemoved = await serDb.SortedSetRemoveAsync(keySer, payload).ConfigureAwait(false);
            var vapeRemoved = await executor.ZRemAsync(keyVape, payload, CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(serScore);
            Assert.NotNull(vapeScore);
            Assert.Equal(serScore.Value, vapeScore.Value, 3);
            Assert.Equal(serRemoved, vapeRemoved > 0);
        }
        finally
        {
            _ = await serDb.KeyDeleteAsync(new RedisKey[] { keySer, keyVape }).ConfigureAwait(false);
            _ = await executor.DeleteAsync(keyVape, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<ConnectionMultiplexer> ConnectStackExchangeAsync(RedisConnectionOptions options)
    {
        var cfg = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectRetry = 3,
            ConnectTimeout = (int)Math.Max(3_000, options.ConnectTimeout.TotalMilliseconds),
            SyncTimeout = 15_000,
            AsyncTimeout = 15_000,
            DefaultDatabase = options.Database,
            Ssl = options.UseTls,
            SslHost = options.UseTls ? (options.TlsHost ?? options.Host) : null,
            User = string.IsNullOrWhiteSpace(options.Username) ? null : options.Username,
            Password = options.Password
        };
        cfg.EndPoints.Add(options.Host, options.Port);

        var mux = await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);
        _ = await mux.GetDatabase(options.Database).PingAsync().ConfigureAwait(false);
        return mux;
    }
}
