using System.Reflection;
using System.Diagnostics;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Connections;

public sealed class RedisCommandExecutorAutoscalerTests
{
    [Fact]
    public void GetMuxLaneSnapshots_ReturnsCurrentLaneState()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 2,
            BulkLaneConnections = 0,
            EnableAutoscaling = false
        });

        var lanes = harness.Executor.GetMuxLaneSnapshots();

        Assert.Equal(2, lanes.Count);
        Assert.All(lanes, lane =>
        {
            Assert.Equal("read-write", lane.Role);
            Assert.True(lane.ConnectionId > 0);
            Assert.True(lane.MaxInFlight > 0);
            Assert.True(lane.Healthy);
            Assert.Equal(0, lane.WriteQueueDepth);
            Assert.Equal(0, lane.InFlight);
            Assert.Equal(0L, lane.BytesSent);
            Assert.Equal(0L, lane.BytesReceived);
            Assert.Equal(0L, lane.Operations);
            Assert.Equal(0L, lane.Failures);
            Assert.Equal(0L, lane.Responses);
            Assert.Equal(0L, lane.OrphanedResponses);
            Assert.Equal(0L, lane.ResponseSequenceMismatches);
            Assert.Equal(0L, lane.TransportResets);
        });
    }

    [Fact]
    public void BulkLaneIsolation_RoutesSeparateLanes_AndFastAutoscalerSignalsStayClean()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 2,
            BulkLaneConnections = 1,
            BulkLaneResponseTimeout = TimeSpan.FromSeconds(8),
            EnableAutoscaling = true,
            MinConnections = 2,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleUpCooldown = TimeSpan.FromMilliseconds(1),
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0.5
        });

        var lanes = harness.Executor.GetMuxLaneSnapshots();
        Assert.Equal(3, lanes.Count);
        Assert.Contains(lanes, lane => lane.Role == "bulk-read-write");
        Assert.Contains(lanes, lane => lane.Role == "read-write");

        var bulkConns = GetConnectionArray(harness.Executor, "_bulkConns");
        Assert.Equal(1, bulkConns.Length);
        var bulkLane = bulkConns.GetValue(0);
        Assert.NotNull(bulkLane);
        SetLaneField(bulkLane!, "_responseTimeoutCount", 100L);

        SetField(harness.Executor, "_lastTimeoutSampleCount", 0L);
        SetField(harness.Executor, "_lastTimeoutSampleTicks", DateTime.UtcNow.AddSeconds(-1).Ticks);
        InvokeEvaluateAutoscale(harness.Executor);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.Equal(2, snapshot.CurrentConnections);
        Assert.True(snapshot.TimeoutRatePerSec < 0.1, $"Expected fast-lane timeout rate near zero, got {snapshot.TimeoutRatePerSec:F3}/s.");
    }

    [Fact]
    public void EmergencyTimeoutSpike_ScalesUp_BoundedByMax()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 1,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 2,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleUpCooldown = TimeSpan.FromMilliseconds(1),
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0.5
        });

        ForceTimeoutSpike(harness.Executor, 10);
        InvokeEvaluateAutoscale(harness.Executor);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.Equal(2, snapshot.CurrentConnections);
        Assert.Equal("up", snapshot.LastScaleDirection);
        Assert.Contains("emergency-timeout-spike", snapshot.LastScaleReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void EmergencyTimeoutSpike_AdvisorMode_DoesNotScale_AndLogsDecision()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 1,
            EnableAutoscaling = true,
            AutoscaleAdvisorMode = true,
            MinConnections = 1,
            MaxConnections = 3,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleUpCooldown = TimeSpan.FromMilliseconds(1),
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0.5
        });

        ForceTimeoutSpike(harness.Executor, 10);
        InvokeEvaluateAutoscale(harness.Executor);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.Equal(1, snapshot.CurrentConnections);
        Assert.Contains(harness.Logger.Messages, m => m.Contains("Autoscaler decision: up(advisor)", StringComparison.Ordinal));
    }

    [Fact]
    public void CooldownPreventsBackToBackEmergencyScaleUp()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 1,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleUpCooldown = TimeSpan.FromMinutes(5),
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0.5
        });

        ForceTimeoutSpike(harness.Executor, 10);
        InvokeEvaluateAutoscale(harness.Executor);
        var first = harness.Executor.GetAutoscalerSnapshot().CurrentConnections;
        Assert.Equal(2, first);

        // Keep cooldown active.
        SetField(harness.Executor, "_lastScaleUpTicks", DateTime.UtcNow.Ticks);
        ForceTimeoutSpike(harness.Executor, 10);
        InvokeEvaluateAutoscale(harness.Executor);

        var second = harness.Executor.GetAutoscalerSnapshot().CurrentConnections;
        Assert.Equal(first, second);
    }

    [Fact]
    public void SustainedLowPressure_ScalesDownToMin_AndNoLower()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 3,
            EnableAutoscaling = true,
            MinConnections = 2,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleDownWindow = TimeSpan.FromMilliseconds(1),
            ScaleDownCooldown = TimeSpan.FromMilliseconds(1),
            ScaleDownP95LatencyMsThreshold = 50
        });

        // Simulate sustained low pressure.
        SetField(harness.Executor, "_lowPressureStreakTicks", TimeSpan.FromSeconds(1).Ticks);
        SetField(harness.Executor, "_lastScaleDownTicks", 0L);
        InvokeEvaluateAutoscale(harness.Executor);
        Thread.Sleep(20);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.Equal(2, snapshot.CurrentConnections);
        Assert.Equal(2, snapshot.MinConnections);
        Assert.Equal("down", snapshot.LastScaleDirection);
    }

    [Fact]
    public void RuntimeDisableAutoscaler_StopsScaleActions()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 2,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleUpCooldown = TimeSpan.FromMilliseconds(1),
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0.5
        });

        var apply = GetMethod(nameof(ApplyAutoscaleOptions));
        apply.Invoke(harness.Executor, new object[]
        {
            new RedisMultiplexerOptions
            {
                Connections = 2,
                EnableAutoscaling = false,
                MinConnections = 1,
                MaxConnections = 4
            }
        });

        ForceTimeoutSpike(harness.Executor, 20);
        InvokeEvaluateAutoscale(harness.Executor);

        Assert.Equal(2, harness.Executor.GetAutoscalerSnapshot().CurrentConnections);
    }

    [Fact]
    public void ReconnectStorm_FreezesAutoscaler_AndBlocksScaleActions()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 2,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ReconnectStormFailureRatePerSecThreshold = 0.5,
            AutoscaleFreezeDuration = TimeSpan.FromMinutes(1)
        });

        SetField(harness.Executor, "_lastFailureSampleCount", -10L);
        SetField(harness.Executor, "_lastFailureSampleTicks", DateTime.UtcNow.AddSeconds(-1).Ticks);
        ForceTimeoutSpike(harness.Executor, 30);
        InvokeEvaluateAutoscale(harness.Executor);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.True(snapshot.Frozen);
        Assert.Equal(2, snapshot.CurrentConnections);
        Assert.Equal(2, snapshot.TargetConnections);
        Assert.Equal("reconnect-storm", snapshot.FreezeReason);
    }

    [Fact]
    public void ScaleRateLimit_FreezesAutoscaler_AndPreventsSecondScaleEvent()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 1,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleUpCooldown = TimeSpan.FromMilliseconds(1),
            EmergencyScaleUpTimeoutRatePerSecThreshold = 0.5,
            MaxScaleEventsPerMinute = 1,
            AutoscaleFreezeDuration = TimeSpan.FromMinutes(1)
        });

        ForceTimeoutSpike(harness.Executor, 20);
        InvokeEvaluateAutoscale(harness.Executor);
        Assert.Equal(2, harness.Executor.GetAutoscalerSnapshot().CurrentConnections);

        SetField(harness.Executor, "_lastScaleUpTicks", 0L);
        ForceTimeoutSpike(harness.Executor, 20);
        InvokeEvaluateAutoscale(harness.Executor);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.True(snapshot.Frozen);
        Assert.Equal("scale-rate-limit", snapshot.FreezeReason);
        Assert.Equal(2, snapshot.CurrentConnections);
    }

    [Fact]
    public void UnhealthyLane_BlocksScaleDown()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 3,
            EnableAutoscaling = true,
            MinConnections = 2,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            ScaleDownWindow = TimeSpan.FromMilliseconds(1),
            ScaleDownCooldown = TimeSpan.FromMilliseconds(1),
            ScaleDownP95LatencyMsThreshold = 50
        });

        SetField(harness.Executor, "_lowPressureStreakTicks", TimeSpan.FromSeconds(1).Ticks);
        SetField(harness.Executor, "_lastScaleDownTicks", 0L);
        MarkFirstLaneUnhealthy(harness.Executor);
        InvokeEvaluateAutoscale(harness.Executor);

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.Equal(3, snapshot.CurrentConnections);
        Assert.Equal(1, snapshot.UnhealthyConnections);
        Assert.Contains(harness.Logger.Messages, m => m.Contains("blocked:unhealthy-lanes", StringComparison.Ordinal));
    }

    [Fact]
    public void FlapDetection_FreezesAutoscaler()
    {
        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 2,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 4,
            AutoscaleSampleInterval = TimeSpan.FromDays(1),
            MaxScaleEventsPerMinute = 10,
            FlapToggleThreshold = 2,
            AutoscaleFreezeDuration = TimeSpan.FromMinutes(1)
        });

        var registerScaleEvent = GetMethod("RegisterScaleEvent");
        var now = DateTime.UtcNow.Ticks;
        registerScaleEvent.Invoke(harness.Executor, new object[] { "up", now });
        registerScaleEvent.Invoke(harness.Executor, new object[] { "down", now + TimeSpan.FromSeconds(1).Ticks });
        registerScaleEvent.Invoke(harness.Executor, new object[] { "up", now + TimeSpan.FromSeconds(2).Ticks });

        var snapshot = harness.Executor.GetAutoscalerSnapshot();
        Assert.True(snapshot.Frozen);
        Assert.Equal("flap-detected", snapshot.FreezeReason);
    }

    [Fact]
    public async Task ScaleDownPath_WaitsForDrainTimeout_WhenInflightNotDrained()
    {
        var waitForDrain = typeof(RedisCommandExecutor).GetMethod("WaitForDrainAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(waitForDrain);
        Assert.Equal(typeof(ValueTask), waitForDrain!.ReturnType);

        using var harness = CreateHarness(new RedisMultiplexerOptions
        {
            Connections = 1,
            EnableAutoscaling = true,
            MinConnections = 1,
            MaxConnections = 2,
            AutoscaleSampleInterval = TimeSpan.FromDays(1)
        });

        var connsField = typeof(RedisCommandExecutor).GetField("_conns", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(connsField);
        var conns = (Array?)connsField!.GetValue(harness.Executor);
        Assert.NotNull(conns);
        Assert.True(conns!.Length > 0);

        var lane = conns.GetValue(0);
        Assert.NotNull(lane);
        var inflightField = lane!.GetType().GetField("_inFlight", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(inflightField);
        var inflight = (SemaphoreSlim?)inflightField!.GetValue(lane);
        Assert.NotNull(inflight);

        Assert.True(inflight!.Wait(0));
        var sw = Stopwatch.StartNew();
        try
        {
            var vt = (ValueTask)waitForDrain.Invoke(null, new object[] { lane, TimeSpan.FromMilliseconds(90), CancellationToken.None })!;
            await vt.AsTask();
        }
        finally
        {
            sw.Stop();
            inflight.Release();
        }

        Assert.True(sw.ElapsedMilliseconds >= 70, $"Expected drain wait near timeout, got {sw.ElapsedMilliseconds}ms.");
    }

    private static void InvokeEvaluateAutoscale(RedisCommandExecutor executor)
    {
        var method = typeof(RedisCommandExecutor).GetMethod("EvaluateAutoscale", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(executor, null);
    }

    private static void ForceTimeoutSpike(RedisCommandExecutor executor, long delta)
    {
        // timeoutRatePerSec = (timeoutTotal - lastCount) / elapsedSec
        // Keep timeoutTotal ~= 0 and push lastCount negative so rate is large.
        SetField(executor, "_lastTimeoutSampleCount", -delta);
        SetField(executor, "_lastTimeoutSampleTicks", DateTime.UtcNow.AddSeconds(-1).Ticks);
    }

    private static MethodInfo GetMethod(string methodName)
    {
        var m = typeof(RedisCommandExecutor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return m!;
    }

    private static void SetField(RedisCommandExecutor executor, string fieldName, object value)
    {
        var f = typeof(RedisCommandExecutor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        f!.SetValue(executor, value);
    }

    private static Array GetConnectionArray(RedisCommandExecutor executor, string fieldName)
    {
        var f = typeof(RedisCommandExecutor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        var value = (Array?)f!.GetValue(executor);
        Assert.NotNull(value);
        return value!;
    }

    private static void SetLaneField(object lane, string fieldName, object value)
    {
        var f = lane.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        f!.SetValue(lane, value);
    }

    private static void MarkFirstLaneUnhealthy(RedisCommandExecutor executor)
    {
        var connsField = typeof(RedisCommandExecutor).GetField("_conns", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(connsField);
        var conns = (Array?)connsField!.GetValue(executor);
        Assert.NotNull(conns);
        Assert.True(conns!.Length > 0);

        var lane = conns.GetValue(0);
        Assert.NotNull(lane);
        var unhealthyField = lane!.GetType().GetField("_consecutiveFailures", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(unhealthyField);
        unhealthyField!.SetValue(lane, 1);
    }

    private static Harness CreateHarness(RedisMultiplexerOptions options)
    {
        var logger = new ListLogger<RedisCommandExecutor>();
        var executor = new RedisCommandExecutor(
            new NoopConnectionFactory(),
            new TestOptionsMonitor<RedisMultiplexerOptions>(options),
            new TestOptionsMonitor<RedisConnectionOptions>(new RedisConnectionOptions()),
            logger);

        return new Harness(executor, logger);
    }

    private sealed class Harness(RedisCommandExecutor executor, ListLogger<RedisCommandExecutor> logger) : IDisposable
    {
        public RedisCommandExecutor Executor { get; } = executor;
        public ListLogger<RedisCommandExecutor> Logger { get; } = logger;

        public void Dispose()
            => Executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class NoopConnectionFactory : IRedisConnectionFactory
    {
        public ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
            => ValueTask.FromResult(new Result<IRedisConnection>(new InvalidOperationException("No network I/O expected in autoscaler unit tests.")));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }

    // keep method name for reflection binding only
    private void ApplyAutoscaleOptions(RedisMultiplexerOptions _)
        => throw new NotSupportedException();
}
