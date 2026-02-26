using OpenTelemetry.Exporter;
using VapeCache.Extensions.Aspire;

namespace VapeCache.Tests.Aspire;

public sealed class VapeCacheTelemetryOptionsTests
{
    [Fact]
    public void UseSeq_SetsHttpOtlpEndpointAndApiKeyHeader()
    {
        var options = new VapeCacheTelemetryOptions()
            .UseSeq("http://seq.internal:5341", "test-key");

        Assert.True(options.EnableOtlpExporter);
        Assert.True(options.UseSeqAsDefaultExporter);
        Assert.Equal("http://seq.internal:5341/ingest/otlp", options.OtlpEndpoint);
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.OtlpProtocol);
        Assert.True(options.OtlpHeaders.TryGetValue("X-Seq-ApiKey", out var key));
        Assert.Equal("test-key", key);
    }

    [Fact]
    public void UseOtlp_Throws_WhenEndpointIsEmpty()
    {
        var options = new VapeCacheTelemetryOptions();

        Assert.Throws<ArgumentException>(() => options.UseOtlp(""));
    }

    [Fact]
    public void AddConfigurations_AppendsCallbacks()
    {
        var options = new VapeCacheTelemetryOptions();
        var metricCalls = 0;
        var tracingCalls = 0;

        options
            .AddMetricsConfiguration(_ => metricCalls++)
            .AddMetricsConfiguration(_ => metricCalls++)
            .AddTracingConfiguration(_ => tracingCalls++)
            .AddTracingConfiguration(_ => tracingCalls++);

        options.ConfigureMetrics?.Invoke(null!);
        options.ConfigureTracing?.Invoke(null!);

        Assert.Equal(2, metricCalls);
        Assert.Equal(2, tracingCalls);
    }
}
