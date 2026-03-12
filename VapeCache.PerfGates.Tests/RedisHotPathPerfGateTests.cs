using System.Diagnostics;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.PerfGates.Tests;

public sealed class RedisHotPathPerfGateTests
{
    private delegate int WriteCommandDelegate(Span<byte> destination);

    [Fact]
    public void RedisRespProtocol_HotPath_ThroughputAndTailLatency_StayWithinBudget()
    {
        const string key = "perf:key:hot:001";
        var setValue = "v"u8.ToArray();
        var mgetKeys = new[] { "k1", "k2", "k3", "k4", "k5", "k6", "k7", "k8" };
        var msetItems = new (string Key, ReadOnlyMemory<byte> Value)[]
        {
            ("k1", "v1"u8.ToArray()),
            ("k2", "value-two"u8.ToArray()),
            ("k3", "value-three"u8.ToArray()),
            ("k4", "value-four"u8.ToArray())
        };

        var cases = new[]
        {
            new PerfCase(
                Name: "GET",
                Length: RedisRespProtocol.GetGetCommandLength(key),
                Writer: dst => RedisRespProtocol.WriteGetCommand(dst, key),
                MinOpsPerSecond: 80_000d,
                MaxP95Us: 25d,
                MaxP99Us: 60d),
            new PerfCase(
                Name: "SET",
                Length: RedisRespProtocol.GetSetCommandLength(key, setValue.Length, ttlMs: null),
                Writer: dst => RedisRespProtocol.WriteSetCommand(dst, key, setValue, ttlMs: null),
                MinOpsPerSecond: 70_000d,
                MaxP95Us: 30d,
                MaxP99Us: 75d),
            new PerfCase(
                Name: "MGET(8 keys)",
                Length: RedisRespProtocol.GetMGetCommandLength(mgetKeys),
                Writer: dst => RedisRespProtocol.WriteMGetCommand(dst, mgetKeys),
                MinOpsPerSecond: 45_000d,
                MaxP95Us: 40d,
                MaxP99Us: 100d),
            new PerfCase(
                Name: "MSET(4 pairs)",
                Length: RedisRespProtocol.GetMSetCommandLength(msetItems),
                Writer: dst => RedisRespProtocol.WriteMSetCommand(dst, msetItems),
                MinOpsPerSecond: 35_000d,
                MaxP95Us: 45d,
                MaxP99Us: 120d)
        };

        foreach (var perfCase in cases)
        {
            var result = Measure(perfCase);
            Assert.True(
                result.ThroughputOpsPerSecond >= perfCase.MinOpsPerSecond,
                $"{perfCase.Name} throughput regression: {result.ThroughputOpsPerSecond:N0} ops/s < {perfCase.MinOpsPerSecond:N0} ops/s");
            Assert.True(
                result.P95Us <= perfCase.MaxP95Us,
                $"{perfCase.Name} p95 regression: {result.P95Us:F2}us > {perfCase.MaxP95Us:F2}us");
            Assert.True(
                result.P99Us <= perfCase.MaxP99Us,
                $"{perfCase.Name} p99 regression: {result.P99Us:F2}us > {perfCase.MaxP99Us:F2}us");
        }
    }

    private static PerfResult Measure(PerfCase perfCase)
    {
        const int warmupIterations = 5_000;
        const int measuredIterations = 30_000;

        var buffer = new byte[perfCase.Length];
        for (var i = 0; i < warmupIterations; i++)
        {
            var written = perfCase.Writer(buffer);
            if (written != perfCase.Length)
                throw new InvalidOperationException($"Unexpected write size during warmup for {perfCase.Name}: {written} != {perfCase.Length}.");
        }

        var latenciesUs = new double[measuredIterations];
        var startAllTicks = Stopwatch.GetTimestamp();

        for (var i = 0; i < measuredIterations; i++)
        {
            var opStart = Stopwatch.GetTimestamp();
            var written = perfCase.Writer(buffer);
            if (written != perfCase.Length)
                throw new InvalidOperationException($"Unexpected write size for {perfCase.Name}: {written} != {perfCase.Length}.");
            var opElapsed = Stopwatch.GetTimestamp() - opStart;
            latenciesUs[i] = opElapsed * 1_000_000d / Stopwatch.Frequency;
        }

        var totalElapsedTicks = Stopwatch.GetTimestamp() - startAllTicks;
        var throughput = measuredIterations / (totalElapsedTicks / (double)Stopwatch.Frequency);
        Array.Sort(latenciesUs);

        return new PerfResult(
            ThroughputOpsPerSecond: throughput,
            P95Us: Percentile(latenciesUs, 0.95),
            P99Us: Percentile(latenciesUs, 0.99));
    }

    private static double Percentile(double[] samples, double percentile)
    {
        if (samples.Length == 0)
            return 0d;

        var rank = (int)Math.Ceiling(percentile * samples.Length) - 1;
        rank = Math.Clamp(rank, 0, samples.Length - 1);
        return samples[rank];
    }

    private sealed record PerfCase(
        string Name,
        int Length,
        WriteCommandDelegate Writer,
        double MinOpsPerSecond,
        double MaxP95Us,
        double MaxP99Us);

    private sealed record PerfResult(double ThroughputOpsPerSecond, double P95Us, double P99Us);
}
