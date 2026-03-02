using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text;
using Xunit;
using Xunit.Sdk;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class CoalescedWritesIntegrationTests
{
    [SkippableFact]
    public async Task CoalescedWrites_SingleCommand()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var key = "vapecache:coalesce:" + Guid.NewGuid().ToString("N");
        var bytes = "hello"u8.ToArray();

        Assert.True(await exec.SetAsync(key, bytes, TimeSpan.FromSeconds(30), ct));
        var got = await exec.GetAsync(key, ct);
        Assert.NotNull(got);
        Assert.Equal(bytes, got);

        Assert.True(await exec.DeleteAsync(key, ct));
        var missing = await exec.GetAsync(key, ct);
        Assert.Null(missing);
    }

    [SkippableFact]
    public async Task CoalescedWrites_Concurrent()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var keyPrefix = "vapecache:coalesce:" + Guid.NewGuid().ToString("N") + ":";

        var tasks = Enumerable.Range(0, 100).Select(async i =>
        {
            var key = keyPrefix + i;
            var payload = BitConverter.GetBytes(i);
            Assert.True(await exec.SetAsync(key, payload, TimeSpan.FromSeconds(60), ct));
            var got = await exec.GetAsync(key, ct);
            Assert.NotNull(got);
            Assert.Equal(i, BitConverter.ToInt32(got));
        });

        await Task.WhenAll(tasks);
    }

    [SkippableFact]
    public async Task CoalescedWrites_Hash_RoundTrip()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        var hashKey = "vapecache:coalesce:hash:" + Guid.NewGuid().ToString("N");
        Assert.True(await exec.HSetAsync(hashKey, "f1", "v1"u8.ToArray(), ct) >= 0);
        var hashVal = await exec.HGetAsync(hashKey, "f1", ct);
        Assert.NotNull(hashVal);
        Assert.Equal("v1", Encoding.UTF8.GetString(hashVal!));
    }

    [SkippableFact]
    public async Task CoalescedWrites_List_RoundTrip()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        var listKey = "vapecache:coalesce:list:" + Guid.NewGuid().ToString("N");

        Assert.True(await exec.LPushAsync(listKey, "a"u8.ToArray(), ct) >= 1);
        Assert.True(await exec.RPushAsync(listKey, "b"u8.ToArray(), ct) >= 2);
        var lrange = await exec.LRangeAsync(listKey, 0, -1, ct);
        Assert.Equal(2, lrange.Length);
        Assert.Equal("a", Encoding.UTF8.GetString(lrange[0]!));
        Assert.Equal("b", Encoding.UTF8.GetString(lrange[1]!));
    }

    [SkippableFact]
    public async Task CoalescedWrites_Set_RoundTrip()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        var setKey = "vapecache:coalesce:set:" + Guid.NewGuid().ToString("N");

        Assert.True(await exec.SAddAsync(setKey, "m1"u8.ToArray(), ct) >= 0);
        Assert.True(await exec.SAddAsync(setKey, "m2"u8.ToArray(), ct) >= 0);
        Assert.True(await exec.SIsMemberAsync(setKey, "m2"u8.ToArray(), ct));
        Assert.Equal(2, await exec.SCardAsync(setKey, ct));
        var members = await exec.SMembersAsync(setKey, ct);
        var membersText = members
            .Where(static m => m is not null)
            .Select(static m => Encoding.UTF8.GetString(m!))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("m1", membersText);
        Assert.Contains("m2", membersText);
    }

    [SkippableFact]
    public async Task CoalescedWrites_SortedSet_RoundTrip()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        var zsetKey = "vapecache:coalesce:zset:" + Guid.NewGuid().ToString("N");

        Assert.True(await exec.ZAddAsync(zsetKey, 10.0, "alice"u8.ToArray(), ct) >= 0);
        Assert.True(await exec.ZAddAsync(zsetKey, 20.0, "bob"u8.ToArray(), ct) >= 0);
        Assert.Equal(2, await exec.ZCardAsync(zsetKey, ct));
        var aliceScore = await exec.ZScoreAsync(zsetKey, "alice"u8.ToArray(), ct);
        var bobRank = await exec.ZRankAsync(zsetKey, "bob"u8.ToArray(), descending: false, ct);
        Assert.Equal(10.0, aliceScore);
        Assert.Equal(1, bobRank);
    }

    [SkippableFact]
    public async Task DirectWrites_Path_RoundTrip_WhenCoalescingDisabled()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = false
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;
        var key = "vapecache:direct:" + Guid.NewGuid().ToString("N");

        Assert.True(await exec.SetAsync(key, "direct"u8.ToArray(), TimeSpan.FromSeconds(30), ct));
        var got = await exec.GetAsync(key, ct);
        Assert.NotNull(got);
        Assert.Equal("direct", Encoding.UTF8.GetString(got!));
        Assert.True(await exec.DeleteAsync(key, ct));
    }

    [SkippableFact]
    public async Task CoalescedWrites_SocketReaderAndDedicatedWorkers_RoundTrip()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 2,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true,
                EnableSocketRespReader = true,
                UseDedicatedLaneWorkers = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        var keyPrefix = "vapecache:tuned:" + Guid.NewGuid().ToString("N");

        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            var key = $"{keyPrefix}:{i}";
            var value = Encoding.UTF8.GetBytes($"v:{i}");
            Assert.True(await exec.SetAsync(key, value, TimeSpan.FromSeconds(30), ct));
            var got = await exec.GetAsync(key, ct);
            Assert.NotNull(got);
            Assert.Equal(value, got);
        });

        await Task.WhenAll(tasks);
    }

    [SkippableFact]
    public async Task CoalescedWrites_Json_RoundTrip_WhenRedisJsonAvailable()
    {
        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var exec = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = 1,
                MaxInFlightPerConnection = 4096,
                EnableCoalescedSocketWrites = true
            }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var modules = await exec.ModuleListAsync(ct);
        var hasRedisJson = modules.Any(static m =>
            !string.IsNullOrWhiteSpace(m) &&
            (m.Contains("rejson", StringComparison.OrdinalIgnoreCase) ||
             m.Contains("json", StringComparison.OrdinalIgnoreCase)));
        Skip.If(!hasRedisJson, "RedisJSON module not installed on test target.");

        var key = "vapecache:coalesce:json:" + Guid.NewGuid().ToString("N");
        var payload = """{"name":"alice","age":34}"""u8.ToArray();

        Assert.True(await exec.JsonSetAsync(key, ".", payload, ct));
        var json = await exec.JsonGetAsync(key, ".", ct);
        Assert.NotNull(json);
        var text = Encoding.UTF8.GetString(json!);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.True(await exec.JsonDelAsync(key, ".", ct) >= 0);
    }
}
