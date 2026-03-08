using VapeCache.Infrastructure.Caching;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using VapeCache.Tests.Telemetry;

namespace VapeCache.Tests.Caching;

[Collection(TelemetryCollection.Name)]
public sealed class CacheTelemetryTests
{
    [Fact]
    public void GetCurrentBackendMeasurement_ReturnsUnknown_WhenNotInitialized()
    {
        CacheTelemetry.ResetForTesting();

        var measurement = CacheTelemetry.GetCurrentBackendMeasurement();

        Assert.Equal(-1, measurement.Value);
    }

    [Theory]
    [InlineData("redis", 1)]
    [InlineData("memory", 0)]
    [InlineData("in-memory", 0)]
    [InlineData("unknown", -1)]
    public void MapBackendName_ReturnsExpectedValue(string backend, int expected)
    {
        Assert.Equal(expected, CacheTelemetry.MapBackendName(backend));
    }

    [Fact]
    public void ObservableGauges_EmitExpectedMeasurements()
    {
        CacheTelemetry.ResetForTesting();
        CacheTelemetry.EnsureInitialized();

        var backendState = new FakeBackendState(BackendType.Redis);
        var spill = new FakeSpillDiagnostics(new SpillStoreDiagnosticsSnapshot(
            SupportsDiskSpill: true,
            SpillToDiskConfigured: true,
            Mode: "noop",
            TotalSpillFiles: 12,
            ActiveShards: 4,
            MaxFilesInShard: 7,
            AvgFilesPerActiveShard: 3,
            ImbalanceRatio: 2.33,
            TopShards: Array.Empty<SpillShardLoad>(),
            SampledAtUtc: DateTimeOffset.UtcNow));

        CacheTelemetry.Initialize(backendState);
        CacheTelemetry.InitializeSpillDiagnostics(spill);

        var measurements = CaptureObservableMeasurements("VapeCache.Cache");

        AssertMetric(measurements, "cache.current.backend", expectedValue: 1, expectedTags: [("backend", "redis")]);
        AssertMetric(measurements, "cache.spill.shard.active", expectedValue: 4, expectedTags: [("mode", "noop")]);
        AssertMetric(measurements, "cache.spill.shard.max_files", expectedValue: 7, expectedTags: [("mode", "noop")]);
        AssertMetric(measurements, "cache.spill.shard.imbalance_ratio", expectedValue: 2.33, expectedTags: [("mode", "noop")]);
    }

    private static Dictionary<string, List<MeasurementSnapshot>> CaptureObservableMeasurements(string meterName)
    {
        var measurements = new Dictionary<string, List<MeasurementSnapshot>>(StringComparer.Ordinal);

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Capture(measurements, instrument, value, tags));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(measurements, instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(measurements, instrument, value, tags));

        listener.Start();
        listener.RecordObservableInstruments();

        return measurements;
    }

    private static void Capture<T>(
        Dictionary<string, List<MeasurementSnapshot>> sink,
        Instrument instrument,
        T value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        if (!sink.TryGetValue(instrument.Name, out var series))
        {
            series = [];
            sink[instrument.Name] = series;
        }

        var convertedTags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            convertedTags[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;

        series.Add(new MeasurementSnapshot(
            Value: value.ToDouble(CultureInfo.InvariantCulture),
            Tags: convertedTags));
    }

    private static void AssertMetric(
        IReadOnlyDictionary<string, List<MeasurementSnapshot>> measurements,
        string metricName,
        double expectedValue,
        params (string Key, string Value)[] expectedTags)
    {
        Assert.True(measurements.ContainsKey(metricName), $"Metric '{metricName}' was not observed.");
        var points = measurements[metricName];
        Assert.NotEmpty(points);

        var match = points.FirstOrDefault(point =>
            Math.Abs(point.Value - expectedValue) < 0.0001 &&
            expectedTags.All(tag => point.Tags.TryGetValue(tag.Key, out var actual) && actual == tag.Value));

        Assert.NotNull(match);
    }

    private sealed record MeasurementSnapshot(double Value, IReadOnlyDictionary<string, string> Tags);

    private sealed class FakeBackendState(BackendType backend) : ICacheBackendState
    {
        public BackendType EffectiveBackend { get; } = backend;
    }

    private sealed class FakeSpillDiagnostics(SpillStoreDiagnosticsSnapshot snapshot) : ISpillStoreDiagnostics
    {
        public SpillStoreDiagnosticsSnapshot GetSnapshot() => snapshot;
    }
}
