using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit.Sdk;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RedisReconnectDrillIntegrationTests
{
    [SkippableFact]
    public async Task ForcedClientKill_ReconnectsAndSustainsTraffic()
    {
        var enabled = TryGetBool("VAPECACHE_RECONNECT_DRILL_ENABLED") ?? false;
        Skip.IfNot(enabled, "Set VAPECACHE_RECONNECT_DRILL_ENABLED=true to run reconnect drill integration.");

        var options = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(options is null, skipReason);

        var connections = Math.Max(1, TryGetInt("VAPECACHE_RECONNECT_DRILL_CONNECTIONS") ?? 4);
        var maxInFlight = Math.Max(64, TryGetInt("VAPECACHE_RECONNECT_DRILL_MAX_INFLIGHT") ?? 2048);
        var workers = Math.Max(1, TryGetInt("VAPECACHE_RECONNECT_DRILL_WORKERS") ?? 48);
        var durationSeconds = Math.Max(5, TryGetInt("VAPECACHE_RECONNECT_DRILL_DURATION_SECONDS") ?? 12);
        var killRounds = Math.Max(1, TryGetInt("VAPECACHE_RECONNECT_DRILL_KILL_ROUNDS") ?? 6);
        var killIntervalMs = Math.Max(100, TryGetInt("VAPECACHE_RECONNECT_DRILL_KILL_INTERVAL_MS") ?? 800);
        var enableCoalescedWrites = TryGetBool("VAPECACHE_RECONNECT_DRILL_COALESCE") ?? true;

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(45, durationSeconds + 30)));
        var ct = testCts.Token;

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(options),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var executor = new RedisCommandExecutor(
            factory,
            Options.Create(new RedisMultiplexerOptions
            {
                Connections = connections,
                MaxInFlightPerConnection = maxInFlight,
                EnableCoalescedSocketWrites = enableCoalescedWrites
            }));

        var keyPrefix = "vapecache:reconnect-drill:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ":";
        var payload = "drill-payload"u8.ToArray();
        var laneNamePrefix = "vapecache-drill-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        await WarmupAsync(executor, keyPrefix, payload, Math.Max(64, connections * 16), ct).ConfigureAwait(false);

        var namedLanes = await AssignLaneClientNamesAsync(executor, laneNamePrefix, ct).ConfigureAwait(false);
        Skip.If(namedLanes == 0, "Could not tag mux lane clients for targeted reconnect drill.");

        await using var adminMux = await ConnectAdminAsync(options, ct).ConfigureAwait(false);
        var server = adminMux.GetServer(options.Host, options.Port);

        long totalOps = 0;
        long totalFailures = 0;
        long totalTimeouts = 0;
        long totalKills = 0;
        var killUnsupported = 0;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));
        var runToken = runCts.Token;

        var loadTasks = Enumerable.Range(0, workers)
            .Select(workerId => Task.Run(async () =>
            {
                var random = new Random(unchecked((Environment.TickCount * 397) ^ workerId));

                while (!runToken.IsCancellationRequested)
                {
                    var key = keyPrefix + random.Next(0, 2048).ToString(CultureInfo.InvariantCulture);

                    try
                    {
                        var setOk = await executor.SetAsync(key, payload, TimeSpan.FromSeconds(30), runToken).ConfigureAwait(false);
                        var got = await executor.GetAsync(key, runToken).ConfigureAwait(false);

                        Interlocked.Increment(ref totalOps);
                        if (!setOk || got is null || got.Length == 0)
                            Interlocked.Increment(ref totalFailures);
                    }
                    catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (TimeoutException)
                    {
                        Interlocked.Increment(ref totalOps);
                        Interlocked.Increment(ref totalFailures);
                        Interlocked.Increment(ref totalTimeouts);
                    }
                    catch
                    {
                        Interlocked.Increment(ref totalOps);
                        Interlocked.Increment(ref totalFailures);
                    }
                }
            }, runToken))
            .ToArray();

        var killerTask = Task.Run(async () =>
        {
            for (var round = 0; round < killRounds && !runToken.IsCancellationRequested; round++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(killIntervalMs), runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    break;
                }

                var targetClientIds = await ReadClientIdsByNamePrefixAsync(server, laneNamePrefix).ConfigureAwait(false);
                if (targetClientIds.Count == 0)
                    continue;

                foreach (var clientId in targetClientIds)
                {
                    if (runToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var result = await server.ExecuteAsync("CLIENT", "KILL", "ID", clientId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                        Interlocked.Add(ref totalKills, ParseKillCount(result));
                    }
                    catch (RedisServerException ex) when (
                        ex.Message.Contains("No such client", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("ERR unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ex.Message.Contains("No such client", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Server policy/module does not allow CLIENT KILL drill on this endpoint.
                        Interlocked.Exchange(ref killUnsupported, 1);
                        return;
                    }
                }
            }
        }, runToken);

        try
        {
            await Task.WhenAll(loadTasks.Append(killerTask)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
        }

        var laneDiagnostics = string.Empty;
        byte[]? probe;
        try
        {
            var probeKey = keyPrefix + "probe";
            Assert.True(await executor.SetAsync(probeKey, payload, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false));
            probe = await executor.GetAsync(probeKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            laneDiagnostics = FormatLaneDiagnostics(executor.GetMuxLaneSnapshots());
            throw new XunitException($"Post-drill probe failed: {ex.Message}. {laneDiagnostics}");
        }

        var ops = Interlocked.Read(ref totalOps);
        var failures = Interlocked.Read(ref totalFailures);
        var kills = Interlocked.Read(ref totalKills);
        laneDiagnostics = FormatLaneDiagnostics(executor.GetMuxLaneSnapshots());

        Skip.If(Volatile.Read(ref killUnsupported) == 1, "Redis user lacks CLIENT LIST/KILL permissions required for reconnect drill.");
        Assert.True(kills > 0, $"Expected targeted CLIENT KILL activity. ops={ops} fail={failures} timeout={Interlocked.Read(ref totalTimeouts)} {laneDiagnostics}");
        Assert.True(ops > 0, "Expected load traffic to execute during reconnect drill.");
        Assert.NotNull(probe);
        Assert.True(
            failures < ops,
            $"Reconnect drill degraded all traffic: ops={ops} fail={failures} timeout={Interlocked.Read(ref totalTimeouts)} kills={kills} {laneDiagnostics}");
    }

    private static async Task WarmupAsync(
        RedisCommandExecutor executor,
        string keyPrefix,
        byte[] payload,
        int operations,
        CancellationToken ct)
    {
        var tasks = Enumerable.Range(0, operations)
            .Select(async i =>
            {
                var key = keyPrefix + "warmup:" + i.ToString(CultureInfo.InvariantCulture);
                await executor.SetAsync(key, payload, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                _ = await executor.GetAsync(key, ct).ConfigureAwait(false);
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<int> AssignLaneClientNamesAsync(RedisCommandExecutor executor, string prefix, CancellationToken ct)
    {
        var connsField = typeof(RedisCommandExecutor).GetField("_conns", BindingFlags.Instance | BindingFlags.NonPublic);
        if (connsField is null)
            return 0;

        var conns = connsField.GetValue(executor) as Array;
        if (conns is null || conns.Length == 0)
            return 0;

        var assigned = 0;

        for (var i = 0; i < conns.Length; i++)
        {
            var lane = conns.GetValue(i);
            if (lane is null)
                continue;

            var executeAsync = lane.GetType().GetMethod(
                "ExecuteAsync",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [typeof(ReadOnlyMemory<byte>), typeof(bool), typeof(CancellationToken)],
                modifiers: null);
            if (executeAsync is null)
                continue;

            try
            {
                var name = $"{prefix}:{i.ToString(CultureInfo.InvariantCulture)}";
                var command = RedisResp.BuildCommand("CLIENT", "SETNAME", name);
                var valueTask = executeAsync.Invoke(lane, [new ReadOnlyMemory<byte>(command), false, ct]);
                if (valueTask is null)
                    continue;

                var asTask = valueTask.GetType().GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public);
                if (asTask?.Invoke(valueTask, null) is not Task responseTask)
                    continue;

                await responseTask.ConfigureAwait(false);

                var result = responseTask.GetType().GetProperty("Result")?.GetValue(responseTask);
                var kind = result?.GetType().GetProperty("Kind")?.GetValue(result)?.ToString();
                var text = result?.GetType().GetProperty("Text")?.GetValue(result)?.ToString();
                if (string.Equals(kind, "SimpleString", StringComparison.Ordinal) &&
                    string.Equals(text, "OK", StringComparison.Ordinal))
                {
                    assigned++;
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return assigned;
    }

    private static async Task<IReadOnlyList<long>> ReadClientIdsByNamePrefixAsync(IServer server, string namePrefix)
    {
        var result = await server.ExecuteAsync("CLIENT", "LIST", "TYPE", "normal").ConfigureAwait(false);
        var payload = result.ToString();
        if (string.IsNullOrWhiteSpace(payload))
            return Array.Empty<long>();

        var ids = new List<long>();
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = 0L;
            string? name = null;

            foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = token.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                    continue;

                var key = token.AsSpan(0, separator);
                var value = token[(separator + 1)..];

                if (key.SequenceEqual("id".AsSpan()))
                {
                    _ = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                }
                else if (key.SequenceEqual("name".AsSpan()))
                {
                    name = value;
                }
            }

            if (id <= 0 || string.IsNullOrWhiteSpace(name))
                continue;

            if (name.StartsWith(namePrefix, StringComparison.Ordinal))
                ids.Add(id);
        }

        return ids;
    }

    private static async Task<ConnectionMultiplexer> ConnectAdminAsync(RedisConnectionOptions options, CancellationToken ct)
    {
        var attempts = new List<RedisConnectionOptions> { options };
        if (!string.IsNullOrWhiteSpace(options.Username) && options.AllowAuthFallbackToPasswordOnly)
            attempts.Add(options with { Username = null });

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            var cfg = new ConfigurationOptions
            {
                AbortOnConnectFail = false,
                ConnectRetry = 3,
                ConnectTimeout = (int)Math.Max(5_000, attempt.ConnectTimeout.TotalMilliseconds),
                SyncTimeout = 15_000,
                AsyncTimeout = 15_000,
                DefaultDatabase = attempt.Database,
                Ssl = attempt.UseTls,
                SslHost = attempt.UseTls ? (attempt.TlsHost ?? attempt.Host) : null,
                User = string.IsNullOrWhiteSpace(attempt.Username) ? null : attempt.Username,
                Password = attempt.Password,
                IncludeDetailInExceptions = false
            };
            cfg.EndPoints.Add(attempt.Host, attempt.Port);

            try
            {
                var mux = await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(5));
                await mux.GetDatabase(attempt.Database).PingAsync().ConfigureAwait(false);
                return mux;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Failed to connect admin multiplexer for reconnect drill.", last);
    }

    private static int ParseKillCount(RedisResult result)
    {
        return int.TryParse(result.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string FormatLaneDiagnostics(IReadOnlyList<RedisMuxLaneSnapshot> lanes)
    {
        if (lanes.Count == 0)
            return "lanes=none";

        return "lanes=" + string.Join(
            ";",
            lanes.Select(static lane =>
                $"[{lane.LaneIndex}:{lane.ConnectionId} ops={lane.Operations} resp={lane.Responses} orphan={lane.OrphanedResponses} mismatch={lane.ResponseSequenceMismatches} resets={lane.TransportResets} fail={lane.Failures} healthy={lane.Healthy}]"));
    }

    private static int? TryGetInt(string key)
        => int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool? TryGetBool(string key)
        => bool.TryParse(Environment.GetEnvironmentVariable(key), out var value)
            ? value
            : null;
}
