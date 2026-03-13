using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using System.Reflection;
using VapeCache.Extensions.Logging;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheSerilogExtensionsInternalsTests
{
    [Fact]
    public void ResolveProtocol_UsesConfiguredValue_ThenHeuristics()
    {
        var grpc = InvokePrivate<OtlpProtocol>(
            "ResolveProtocol",
            "grpc",
            new Uri("http://localhost:4318", UriKind.Absolute));
        Assert.Equal(OtlpProtocol.Grpc, grpc);

        var httpByPort = InvokePrivate<OtlpProtocol>(
            "ResolveProtocol",
            null,
            new Uri("http://localhost:4318", UriKind.Absolute));
        Assert.Equal(OtlpProtocol.HttpProtobuf, httpByPort);

        var httpByPath = InvokePrivate<OtlpProtocol>(
            "ResolveProtocol",
            null,
            new Uri("http://localhost:5341/ingest/otlp", UriKind.Absolute));
        Assert.Equal(OtlpProtocol.HttpProtobuf, httpByPath);

        var grpcByFallback = InvokePrivate<OtlpProtocol>(
            "ResolveProtocol",
            "bad-value",
            new Uri("http://localhost:4317", UriKind.Absolute));
        Assert.Equal(OtlpProtocol.Grpc, grpcByFallback);
    }

    [Fact]
    public void ResolveSignalEndpoint_HandlesAllKnownShapes()
    {
        var alreadySignal = InvokePrivate<Uri>(
            "ResolveSignalEndpoint",
            new Uri("http://localhost:4318/v1/logs", UriKind.Absolute),
            "logs");
        Assert.Equal("http://localhost:4318/v1/logs", alreadySignal.ToString());

        var v1Base = InvokePrivate<Uri>(
            "ResolveSignalEndpoint",
            new Uri("http://localhost:4318/v1", UriKind.Absolute),
            "logs");
        Assert.Equal("http://localhost:4318/v1/logs", v1Base.ToString());

        var ingest = InvokePrivate<Uri>(
            "ResolveSignalEndpoint",
            new Uri("http://localhost:5341/ingest/otlp", UriKind.Absolute),
            "logs");
        Assert.Equal("http://localhost:5341/ingest/otlp/v1/logs", ingest.ToString());

        var otherV1Signal = InvokePrivate<Uri>(
            "ResolveSignalEndpoint",
            new Uri("http://localhost:4318/v1/traces", UriKind.Absolute),
            "logs");
        Assert.Equal("http://localhost:4318/v1/traces", otherV1Signal.ToString());

        var defaultAppend = InvokePrivate<Uri>(
            "ResolveSignalEndpoint",
            new Uri("http://localhost:4318", UriKind.Absolute),
            "logs");
        Assert.Equal("http://localhost:4318/v1/logs", defaultAppend.ToString());
    }

    [Fact]
    public void BuildResourceAttributes_UsesConfigurationAndEnvironmentFallbacks()
    {
        var configurationFromApp = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:Properties:Application"] = "vc-tests"
        });

        var attrs1 = InvokePrivate<Dictionary<string, object>>(
            "BuildResourceAttributes",
            configurationFromApp);

        Assert.Equal("vc-tests", attrs1["service.name"]);

        var previousService = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        var previousAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "otel-fallback");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);

            var configurationEmpty = BuildConfig();
            var attrs2 = InvokePrivate<Dictionary<string, object>>(
                "BuildResourceAttributes",
                configurationEmpty);

            Assert.Equal("otel-fallback", attrs2["service.name"]);
            Assert.Equal("Production", attrs2["deployment.environment"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", previousService);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspnet);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
        }
    }

    [Fact]
    public void HasConfiguredSink_DetectsConfiguredAndMissingSink()
    {
        var configuration = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:WriteTo:0:Name"] = "Console",
            ["Serilog:WriteTo:1:Name"] = "File"
        });

        var hasConsole = InvokePrivate<bool>("HasConfiguredSink", configuration, "Console");
        var hasSeq = InvokePrivate<bool>("HasConfiguredSink", configuration, "Seq");

        Assert.True(hasConsole);
        Assert.False(hasSeq);
    }

    [Fact]
    public void ResolveLogLevel_AndResolveRollingInterval_HandleFallbacks()
    {
        var parsedLevel = InvokePrivate<LogEventLevel>("ResolveLogLevel", "Warning", LogEventLevel.Information);
        var fallbackLevel = InvokePrivate<LogEventLevel>("ResolveLogLevel", "bogus", LogEventLevel.Error);
        Assert.Equal(LogEventLevel.Warning, parsedLevel);
        Assert.Equal(LogEventLevel.Error, fallbackLevel);

        var parsedRolling = InvokePrivate<RollingInterval>("ResolveRollingInterval", "Hour");
        var fallbackRolling = InvokePrivate<RollingInterval>("ResolveRollingInterval", "bogus");
        Assert.Equal(RollingInterval.Hour, parsedRolling);
        Assert.Equal(RollingInterval.Day, fallbackRolling);
    }

    [Fact]
    public void ConfigureVapeCacheLogging_WithSeqAndOpenTelemetryAndGroceryStoreBranches_DoesNotThrow()
    {
        var previousVerbose = Environment.GetEnvironmentVariable("VAPECACHE_GROCERYSTORE_VERBOSE");
        try
        {
            Environment.SetEnvironmentVariable("VAPECACHE_GROCERYSTORE_VERBOSE", "true");

            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["Serilog:FallbackConsole:Enabled"] = "false",
                ["Serilog:File:Enabled"] = "false",
                ["Serilog:Seq:Enabled"] = "true",
                ["Serilog:Seq:ServerUrl"] = "http://localhost:5341",
                ["Serilog:OpenTelemetry:Enabled"] = "true",
                ["Serilog:OpenTelemetry:Endpoint"] = "http://localhost:4318",
                ["Serilog:OpenTelemetry:Protocol"] = "http/protobuf",
                ["GroceryStoreStress:Enabled"] = "true"
            });

            using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
            using var logger = new LoggerConfiguration()
                .ConfigureVapeCacheLogging(config, services, environmentName: "Production")
                .CreateLogger();

            logger.Warning("coverage-branch-test");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAPECACHE_GROCERYSTORE_VERBOSE", previousVerbose);
        }
    }

    [Fact]
    public void ConfigureVapeCacheLogging_WithJsonConsoleAndPreconfiguredWriteTo_DoesNotThrow()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:FallbackConsole:Enabled"] = "true",
            ["Serilog:File:Enabled"] = "false",
            ["Serilog:Json:Enabled"] = "true",
            ["Serilog:Json:ConsoleEnabled"] = "true",
            ["Serilog:WriteTo:0:Name"] = "Console"
        });

        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        using var logger = new LoggerConfiguration()
            .ConfigureVapeCacheLogging(config, services, environmentName: "Development")
            .CreateLogger();

        logger.Information("coverage-json-console");
    }

    private static IConfiguration BuildConfig(IReadOnlyDictionary<string, string?>? values = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
    }

    private static T InvokePrivate<T>(string methodName, params object?[] args)
    {
        var methods = typeof(VapeCacheSerilogExtensions)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length)
                     ?? throw new InvalidOperationException($"Method '{methodName}' with {args.Length} args not found.");

        var result = method.Invoke(null, args);
        if (result is T typed)
            return typed;

        if (result is null && default(T) is null)
            return default!;

        throw new InvalidOperationException($"Unexpected return type from '{methodName}'.");
    }
}
