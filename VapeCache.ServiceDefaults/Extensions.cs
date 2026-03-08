using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private static readonly PathString[] TelemetryExcludedPaths =
    [
        new PathString(HealthEndpointPath),
        new PathString(AlivenessEndpointPath),
        new PathString("/vapecache/status"),
        new PathString("/vapecache/stats"),
        new PathString("/vapecache/stream"),
        new PathString("/vapecache/dashboard"),
        new PathString("/dashboard"),
        new PathString("/_blazor")
    ];

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        tracing.Filter = static context => ShouldIncludeAspNetTelemetry(context.Request.Path)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var endpoint = ResolveOtlpEndpoint(builder.Configuration);

        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            var protocol = ResolveOtlpProtocol(builder.Configuration, endpointUri);
            builder.Services.AddOpenTelemetry().UseOtlpExporter(
                protocol: protocol,
                baseUrl: endpointUri);
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    private static string? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var endpoint = configuration["OpenTelemetry:Otlp:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
            return endpoint;

        endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(endpoint))
            return endpoint;

        endpoint = configuration["DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"];
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
    }

    private static OtlpExportProtocol ResolveOtlpProtocol(IConfiguration configuration, Uri endpoint)
    {
        var configured = configuration["OpenTelemetry:Otlp:Protocol"];
        if (TryParseOtlpProtocol(configured, out var protocol))
            return protocol;

        configured = configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
        if (TryParseOtlpProtocol(configured, out protocol))
            return protocol;

        return InferOtlpProtocol(endpoint);
    }

    private static bool TryParseOtlpProtocol(string? configured, out OtlpExportProtocol protocol)
    {
        protocol = default;
        if (string.IsNullOrWhiteSpace(configured))
            return false;

        var normalized = configured.Trim().ToLowerInvariant();
        if (normalized is "http/protobuf" or "http-protobuf" or "httpprotobuf")
        {
            protocol = OtlpExportProtocol.HttpProtobuf;
            return true;
        }

        if (normalized is "grpc")
        {
            protocol = OtlpExportProtocol.Grpc;
            return true;
        }

        return Enum.TryParse(configured, ignoreCase: true, out protocol);
    }

    private static OtlpExportProtocol InferOtlpProtocol(Uri endpoint)
    {
        if (endpoint.Port == 4318)
            return OtlpExportProtocol.HttpProtobuf;

        if (endpoint.Port == 5341)
            return OtlpExportProtocol.HttpProtobuf;

        if (endpoint.AbsolutePath.Contains("/ingest/otlp", StringComparison.OrdinalIgnoreCase))
            return OtlpExportProtocol.HttpProtobuf;

        if (endpoint.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
            return OtlpExportProtocol.HttpProtobuf;

        return OtlpExportProtocol.Grpc;
    }

    private static bool ShouldIncludeAspNetTelemetry(PathString path)
    {
        for (var i = 0; i < TelemetryExcludedPaths.Length; i++)
        {
            if (path.StartsWithSegments(TelemetryExcludedPaths[i]))
                return false;
        }

        return true;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
