using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using System.Globalization;

namespace VapeCache.Infrastructure.DependencyInjection;

/// <summary>
/// Centralizes Serilog host configuration in the infrastructure boundary.
/// </summary>
public static class VapeCacheSerilogExtensions
{
    private const string DefaultConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}:{SpanId}) {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Executes configure vape cache logging.
    /// </summary>
    public static LoggerConfiguration ConfigureVapeCacheLogging(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IServiceProvider services)
    {
        loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

        ConfigureSeqSink(configuration, loggerConfiguration);
        ConfigureFallbackConsoleSink(configuration, loggerConfiguration);
        ConfigureOpenTelemetrySink(configuration, loggerConfiguration);
        ConfigureGroceryStoreOverrides(configuration, loggerConfiguration);
        return loggerConfiguration;
    }

    private static void ConfigureSeqSink(IConfiguration configuration, LoggerConfiguration loggerConfiguration)
    {
        var enabled = configuration.GetValue<bool?>("Serilog:Seq:Enabled") ?? false;
        if (!enabled || HasConfiguredSink(configuration, "Seq"))
            return;

        var serverUrl = configuration["Serilog:Seq:ServerUrl"];
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            return;

        var apiKey = configuration["Serilog:Seq:ApiKey"];
        var restrictedToMinimumLevel = ResolveLogLevel(configuration["Serilog:Seq:RestrictedToMinimumLevel"]);
        var batchPostingLimit = Math.Max(1, configuration.GetValue<int?>("Serilog:Seq:BatchPostingLimit") ?? 1000);
        var periodMilliseconds = Math.Max(200, configuration.GetValue<int?>("Serilog:Seq:PeriodMs") ?? 2000);

        loggerConfiguration.WriteTo.Seq(
            serverUrl,
            apiKey: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            restrictedToMinimumLevel: restrictedToMinimumLevel,
            batchPostingLimit: batchPostingLimit,
            period: TimeSpan.FromMilliseconds(periodMilliseconds),
            formatProvider: CultureInfo.InvariantCulture);
    }

    private static void ConfigureFallbackConsoleSink(IConfiguration configuration, LoggerConfiguration loggerConfiguration)
    {
        var enabled = configuration.GetValue<bool?>("Serilog:FallbackConsole:Enabled") ?? true;
        if (!enabled || HasConfiguredSink(configuration, "Console"))
            return;

        var outputTemplate = configuration["Serilog:FallbackConsole:OutputTemplate"];
        if (string.IsNullOrWhiteSpace(outputTemplate))
            outputTemplate = DefaultConsoleOutputTemplate;

        loggerConfiguration.WriteTo.Console(
            outputTemplate: outputTemplate,
            formatProvider: CultureInfo.InvariantCulture);
    }

    private static LogEventLevel ResolveLogLevel(string? configuredLevel)
    {
        return Enum.TryParse(configuredLevel, ignoreCase: true, out LogEventLevel parsedLevel)
            ? parsedLevel
            : LogEventLevel.Information;
    }

    private static bool HasConfiguredSink(IConfiguration configuration, string sinkName)
    {
        var writeToSection = configuration.GetSection("Serilog:WriteTo");
        foreach (var sink in writeToSection.GetChildren())
        {
            var configuredName = sink["Name"];
            if (string.Equals(configuredName, sinkName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ConfigureGroceryStoreOverrides(IConfiguration configuration, LoggerConfiguration loggerConfiguration)
    {
        var groceryStoreEnabled = configuration.GetValue<bool?>("GroceryStoreStress:Enabled") ?? false;
        if (!groceryStoreEnabled)
            return;

        var verbose = string.Equals(
            Environment.GetEnvironmentVariable("VAPECACHE_GROCERYSTORE_VERBOSE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("VapeCache.Console.GroceryStore", verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("VapeCache.Infrastructure", verbose ? LogEventLevel.Information : LogEventLevel.Warning);
    }

    private static void ConfigureOpenTelemetrySink(IConfiguration configuration, LoggerConfiguration loggerConfiguration)
    {
        var enabled = configuration.GetValue<bool?>("Serilog:OpenTelemetry:Enabled") ?? true;
        if (!enabled)
            return;

        var endpoint = configuration["Serilog:OpenTelemetry:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = configuration["OpenTelemetry:Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            return;

        var configuredProtocol =
            configuration["Serilog:OpenTelemetry:Protocol"] ??
            configuration["OpenTelemetry:Otlp:Protocol"] ??
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");

        var protocol = ResolveProtocol(configuredProtocol, endpointUri);
        var sinkEndpoint = protocol == OtlpProtocol.HttpProtobuf
            ? ResolveSignalEndpoint(endpointUri, "logs")
            : endpointUri;

        loggerConfiguration.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = sinkEndpoint.ToString();
            options.Protocol = protocol;
            options.ResourceAttributes = BuildResourceAttributes(configuration);
        });
    }

    private static OtlpProtocol ResolveProtocol(string? configuredProtocol, Uri endpoint)
    {
        if (TryParseOtlpProtocol(configuredProtocol, out var parsed))
            return parsed;

        var isHttpProtobuf = endpoint.Port == 4318 ||
                             endpoint.Port == 5341 ||
                             endpoint.AbsolutePath.Contains("/ingest/otlp", StringComparison.OrdinalIgnoreCase) ||
                             endpoint.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase);

        return isHttpProtobuf ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
    }

    private static bool TryParseOtlpProtocol(string? configuredProtocol, out OtlpProtocol protocol)
    {
        protocol = default;
        if (string.IsNullOrWhiteSpace(configuredProtocol))
            return false;

        var normalized = configuredProtocol.Trim().ToLowerInvariant();
        if (normalized is "http/protobuf" or "http-protobuf" or "httpprotobuf")
        {
            protocol = OtlpProtocol.HttpProtobuf;
            return true;
        }

        if (normalized is "grpc")
        {
            protocol = OtlpProtocol.Grpc;
            return true;
        }

        return Enum.TryParse(configuredProtocol, ignoreCase: true, out protocol);
    }

    private static Dictionary<string, object> BuildResourceAttributes(IConfiguration configuration)
    {
        var serviceName = configuration["Serilog:Properties:Application"];
        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        serviceName ??= "VapeCache.Console";

        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(environment))
            environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        environment ??= "Production";

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["service.name"] = serviceName,
            ["deployment.environment"] = environment
        };
    }

    private static Uri ResolveSignalEndpoint(Uri endpoint, string signal)
    {
        var endpointText = endpoint.ToString().TrimEnd('/');
        var signalSuffix = $"/v1/{signal}";

        if (endpointText.EndsWith(signalSuffix, StringComparison.OrdinalIgnoreCase))
            return endpoint;

        if (endpointText.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{endpointText}/{signal}", UriKind.Absolute);

        if (endpointText.EndsWith("/ingest/otlp", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{endpointText}{signalSuffix}", UriKind.Absolute);

        if (endpointText.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        return new Uri($"{endpointText}{signalSuffix}", UriKind.Absolute);
    }
}
