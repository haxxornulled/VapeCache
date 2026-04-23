using System.Diagnostics.Metrics;
using System.Globalization;
using VapeCache.Infrastructure.Connections;
using VapeCache.Tests.Telemetry;

namespace VapeCache.Tests.Connections;

[Collection(TelemetryCollection.Name)]
public sealed class RedisTelemetryTests
{
    [Fact]
    public void ObservableMetrics_EmitBaseline_WhenNoProvidersRegistered()
    {
        RedisTelemetry.ResetForTesting();
        RedisTelemetry.EnsureInitialized();

        var measurements = CaptureObservableMeasurements("VapeCache.Redis");

        AssertMetric(measurements, "redis.queue.depth", expectedValue: 0, expectedTags: [("queue", "writes"), ("connection.id", "-1")]);
        AssertMetric(measurements, "redis.queue.depth", expectedValue: 0, expectedTags: [("queue", "pending"), ("connection.id", "-1")]);

        AssertMetric(measurements, "redis.mux.lane.bytes.sent", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.bytes.received", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.operations", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.responses", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.failures", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.responses.orphaned", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.response.sequence.mismatches", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.transport.resets", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.mux.lane.inflight", expectedValue: 0, expectedTags: [("connection.id", "-1"), ("max_inflight", "0")]);
        AssertMetric(measurements, "redis.mux.lane.inflight.utilization", expectedValue: 0, expectedTags: [("connection.id", "-1")]);
        AssertMetric(measurements, "redis.parser.frames_per_sec", expectedValue: 0, expectedTags: [("scope", "global")]);
        AssertMetric(measurements, "redis.parser.bytes_per_sec", expectedValue: 0, expectedTags: [("scope", "global")]);
    }

    [Fact]
    public void ObservableMetrics_UseRegisteredProviders_WhenAvailable()
    {
        RedisTelemetry.ResetForTesting();
        RedisTelemetry.EnsureInitialized();

        const int connectionId = 42;
        RedisTelemetry.RegisterQueueDepthProvider(connectionId, () => new RedisTelemetry.QueueDepthSnapshot(
            Writes: 11,
            Pending: 3,
            WritesCapacity: 128,
            PendingCapacity: 64));
        RedisTelemetry.RegisterMuxLaneUsageProvider(connectionId, () => new RedisTelemetry.MuxLaneUsageSnapshot(
            BytesSent: 1000,
            BytesReceived: 1200,
            Operations: 77,
            Failures: 2,
            Responses: 75,
            OrphanedResponses: 1,
            ResponseSequenceMismatches: 4,
            TransportResets: 5,
            InFlight: 6,
            MaxInFlight: 20));

        try
        {
            var measurements = CaptureObservableMeasurements("VapeCache.Redis");

            AssertMetric(measurements, "redis.queue.depth", expectedValue: 11, expectedTags: [("queue", "writes"), ("connection.id", "42")]);
            AssertMetric(measurements, "redis.queue.depth", expectedValue: 3, expectedTags: [("queue", "pending"), ("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.bytes.sent", expectedValue: 1000, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.bytes.received", expectedValue: 1200, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.operations", expectedValue: 77, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.responses", expectedValue: 75, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.failures", expectedValue: 2, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.responses.orphaned", expectedValue: 1, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.response.sequence.mismatches", expectedValue: 4, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.transport.resets", expectedValue: 5, expectedTags: [("connection.id", "42")]);
            AssertMetric(measurements, "redis.mux.lane.inflight", expectedValue: 6, expectedTags: [("connection.id", "42"), ("max_inflight", "20")]);
            AssertMetric(measurements, "redis.mux.lane.inflight.utilization", expectedValue: 0.3, expectedTags: [("connection.id", "42")]);

            AssertNoMetricTagValue(measurements, "redis.mux.lane.bytes.sent", "connection.id", "-1");
        }
        finally
        {
            RedisTelemetry.UnregisterQueueDepthProvider(connectionId);
            RedisTelemetry.UnregisterMuxLaneUsageProvider(connectionId);
            RedisTelemetry.ResetForTesting();
        }
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

    private static void AssertNoMetricTagValue(
        IReadOnlyDictionary<string, List<MeasurementSnapshot>> measurements,
        string metricName,
        string tagKey,
        string forbiddenValue)
    {
        Assert.True(measurements.ContainsKey(metricName), $"Metric '{metricName}' was not observed.");
        var hasForbidden = measurements[metricName]
            .Any(point => point.Tags.TryGetValue(tagKey, out var value) && value == forbiddenValue);
        Assert.False(hasForbidden, $"Metric '{metricName}' unexpectedly contained {tagKey}={forbiddenValue}.");
    }

    private sealed record MeasurementSnapshot(double Value, IReadOnlyDictionary<string, string> Tags);
}
