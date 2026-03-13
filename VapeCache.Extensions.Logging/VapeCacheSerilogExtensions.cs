using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.OpenTelemetry;
using System.Globalization;
using VapeCache.Guards;

namespace VapeCache.Extensions.Logging;

/// <summary>
/// Centralized Serilog host configuration for VapeCache hosts.
/// </summary>
public static class VapeCacheSerilogExtensions
{
    private const string DefaultConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}:{SpanId}) {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Configures Serilog sinks and defaults for VapeCache hosts.
    /// </summary>
    public static LoggerConfiguration ConfigureVapeCacheLogging(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IServiceProvider services,
        string? environmentName = null)
    {
        ParanoiaThrowGuard.Against.NotNull(loggerConfiguration);
        ParanoiaThrowGuard.Against.NotNull(configuration);
        ParanoiaThrowGuard.Against.NotNull(services);

        ApplyEnvironmentMinimumLevelDefaults(loggerConfiguration, configuration, environmentName);
        var jsonFormatterResolver = ResolveJsonFormatterResolver(services);

        loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

        ConfigureSeqSink(configuration, loggerConfiguration);
        ConfigureFallbackConsoleSink(configuration, loggerConfiguration, jsonFormatterResolver);
        ConfigureFileSink(configuration, loggerConfiguration, environmentName, jsonFormatterResolver);
        ConfigureOpenTelemetrySink(configuration, loggerConfiguration);
        ConfigureGroceryStoreOverrides(configuration, loggerConfiguration);
        return loggerConfiguration;
    }

    private static IVapeCacheJsonLogFormatterResolver ResolveJsonFormatterResolver(IServiceProvider services)
    {
        return services.GetService(typeof(IVapeCacheJsonLogFormatterResolver)) as IVapeCacheJsonLogFormatterResolver
               ?? DefaultVapeCacheJsonLogFormatterResolver.Instance;
    }

    private static void ApplyEnvironmentMinimumLevelDefaults(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string? environmentName)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Serilog:MinimumLevel:Default"]))
            return;

        var isProduction = string.IsNullOrWhiteSpace(environmentName) ||
                           environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase);

        if (!isProduction)
            return;

        loggerConfiguration
            .MinimumLevel.Warning()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning);
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
        var restrictedToMinimumLevel = ResolveLogLevel(configuration["Serilog:Seq:RestrictedToMinimumLevel"], LogEventLevel.Information);
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

    private static void ConfigureFallbackConsoleSink(
        IConfiguration configuration,
        LoggerConfiguration loggerConfiguration,
        IVapeCacheJsonLogFormatterResolver jsonFormatterResolver)
    {
        var enabled = configuration.GetValue<bool?>("Serilog:FallbackConsole:Enabled") ?? true;
        if (!enabled || HasConfiguredSink(configuration, "Console"))
            return;

        var formatter = jsonFormatterResolver.ResolveFormatter(configuration, VapeCacheJsonSinkTarget.Console);
        if (formatter is not null)
        {
            loggerConfiguration.WriteTo.Console(formatter);
            return;
        }

        var outputTemplate = configuration["Serilog:FallbackConsole:OutputTemplate"];
        if (string.IsNullOrWhiteSpace(outputTemplate))
            outputTemplate = DefaultConsoleOutputTemplate;
        
        loggerConfiguration.WriteTo.Console(
            outputTemplate: outputTemplate,
            formatProvider: CultureInfo.InvariantCulture);
    }

    private static void ConfigureFileSink(
        IConfiguration configuration,
        LoggerConfiguration loggerConfiguration,
        string? environmentName,
        IVapeCacheJsonLogFormatterResolver jsonFormatterResolver)
    {
        var enabled = configuration.GetValue<bool?>("Serilog:File:Enabled") ?? false;
        if (!enabled || HasConfiguredSink(configuration, "File"))
            return;

        var path = configuration["Serilog:File:Path"];
        if (string.IsNullOrWhiteSpace(path))
            path = "logs/vapecache-.log";

        var outputTemplate = configuration["Serilog:File:OutputTemplate"];
        if (string.IsNullOrWhiteSpace(outputTemplate))
            outputTemplate = DefaultConsoleOutputTemplate;

        var isProduction = string.IsNullOrWhiteSpace(environmentName) ||
                           environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase);
        var defaultLevel = isProduction ? LogEventLevel.Warning : LogEventLevel.Information;
        var restrictedToMinimumLevel = ResolveLogLevel(configuration["Serilog:File:RestrictedToMinimumLevel"], defaultLevel);

        var retainedFileCountLimit = configuration.GetValue<int?>("Serilog:File:RetainedFileCountLimit") ?? 14;
        var fileSizeLimitBytes = configuration.GetValue<long?>("Serilog:File:FileSizeLimitBytes") ?? 104_857_600;
        var rollOnFileSizeLimit = configuration.GetValue<bool?>("Serilog:File:RollOnFileSizeLimit") ?? true;
        var shared = configuration.GetValue<bool?>("Serilog:File:Shared") ?? true;
        var flushSeconds = Math.Max(1, configuration.GetValue<int?>("Serilog:File:FlushToDiskIntervalSeconds") ?? 2);
        var rollingInterval = ResolveRollingInterval(configuration["Serilog:File:RollingInterval"]);
        var jsonFormatter = jsonFormatterResolver.ResolveFormatter(configuration, VapeCacheJsonSinkTarget.File);

        if (jsonFormatter is not null)
        {
            ConfigureJsonFileSink(
                loggerConfiguration,
                jsonFormatter,
                path,
                restrictedToMinimumLevel,
                rollingInterval,
                retainedFileCountLimit,
                fileSizeLimitBytes,
                rollOnFileSizeLimit,
                shared,
                flushSeconds);
            return;
        }

        loggerConfiguration.WriteTo.File(
            path: path,
            restrictedToMinimumLevel: restrictedToMinimumLevel,
            outputTemplate: outputTemplate,
            formatProvider: CultureInfo.InvariantCulture,
            rollingInterval: rollingInterval,
            retainedFileCountLimit: retainedFileCountLimit,
            fileSizeLimitBytes: fileSizeLimitBytes,
            rollOnFileSizeLimit: rollOnFileSizeLimit,
            shared: shared,
            flushToDiskInterval: TimeSpan.FromSeconds(flushSeconds));
    }

    private static void ConfigureJsonFileSink(
        LoggerConfiguration loggerConfiguration,
        ITextFormatter formatter,
        string path,
        LogEventLevel restrictedToMinimumLevel,
        RollingInterval rollingInterval,
        int retainedFileCountLimit,
        long fileSizeLimitBytes,
        bool rollOnFileSizeLimit,
        bool shared,
        int flushSeconds)
    {
        loggerConfiguration.WriteTo.File(
            formatter: formatter,
            path: path,
            restrictedToMinimumLevel: restrictedToMinimumLevel,
            rollingInterval: rollingInterval,
            retainedFileCountLimit: retainedFileCountLimit,
            fileSizeLimitBytes: fileSizeLimitBytes,
            rollOnFileSizeLimit: rollOnFileSizeLimit,
            shared: shared,
            flushToDiskInterval: TimeSpan.FromSeconds(flushSeconds));
    }

    private static LogEventLevel ResolveLogLevel(string? configuredLevel, LogEventLevel fallbackLevel)
    {
        return Enum.TryParse(configuredLevel, ignoreCase: true, out LogEventLevel parsedLevel)
            ? parsedLevel
            : fallbackLevel;
    }

    private static RollingInterval ResolveRollingInterval(string? configuredRollingInterval)
    {
        return Enum.TryParse(configuredRollingInterval, ignoreCase: true, out RollingInterval parsed)
            ? parsed
            : RollingInterval.Day;
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
        serviceName ??= "VapeCache";

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
