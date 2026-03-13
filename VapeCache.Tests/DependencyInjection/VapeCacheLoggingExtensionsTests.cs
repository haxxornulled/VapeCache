using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;
using VapeCache.Extensions.Logging;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheLoggingExtensionsTests
{
    [Fact]
    public void ConfigureVapeCacheLogging_ThrowsWhenLoggerConfigurationIsNull()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("throw-null-logger"));

        Assert.Throws<ArgumentNullException>(() =>
            VapeCacheSerilogExtensions.ConfigureVapeCacheLogging(
                null!,
                configuration,
                services,
                environmentName: "Production"));
    }

    [Fact]
    public void ConfigureVapeCacheLogging_ThrowsWhenConfigurationIsNull()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(() =>
            new LoggerConfiguration().ConfigureVapeCacheLogging(
                null!,
                services,
                environmentName: "Production"));
    }

    [Fact]
    public void ConfigureVapeCacheLogging_ThrowsWhenServicesIsNull()
    {
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("throw-null-services"));

        Assert.Throws<ArgumentNullException>(() =>
            new LoggerConfiguration().ConfigureVapeCacheLogging(
                configuration,
                null!,
                environmentName: "Production"));
    }

    [Fact]
    public void ConfigureVapeCacheLogging_ProductionDefault_UsesWarningFloorForFileSink()
    {
        var logFile = Path.Combine(Path.GetTempPath(), $"vapecache-log-prod-{Guid.NewGuid():N}.log");
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var configuration = BuildFileOnlyConfig(logFile);

            using (var logger = new LoggerConfiguration()
                       .ConfigureVapeCacheLogging(configuration, services, environmentName: "Production")
                       .CreateLogger())
            {
                logger.Information("production-info-should-not-be-written");
                logger.Warning("production-warning-should-be-written");
            }

            var content = ReadAllTextEventually(logFile, TimeSpan.FromSeconds(5));
            Assert.Contains("production-warning-should-be-written", content, StringComparison.Ordinal);
            Assert.DoesNotContain("production-info-should-not-be-written", content, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(logFile);
        }
    }

    [Fact]
    public void DefaultResolver_ReturnsNull_WhenJsonDisabled()
    {
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("resolver-disabled"), new Dictionary<string, string?>
        {
            ["Serilog:Json:Enabled"] = "false"
        });

        var formatter = DefaultVapeCacheJsonLogFormatterResolver.Instance
            .ResolveFormatter(configuration, VapeCacheJsonSinkTarget.File);

        Assert.Null(formatter);
    }

    [Fact]
    public void DefaultResolver_ReturnsCompactByDefault_WhenEnabledForFile()
    {
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("resolver-compact"), new Dictionary<string, string?>
        {
            ["Serilog:Json:Enabled"] = "true",
            ["Serilog:Json:FileEnabled"] = "true"
        });

        var formatter = DefaultVapeCacheJsonLogFormatterResolver.Instance
            .ResolveFormatter(configuration, VapeCacheJsonSinkTarget.File);

        Assert.IsType<CompactJsonFormatter>(formatter);
    }

    [Fact]
    public void DefaultResolver_ReturnsRenderedCompact_WhenConfigured()
    {
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("resolver-rendered"), new Dictionary<string, string?>
        {
            ["Serilog:Json:Enabled"] = "true",
            ["Serilog:Json:FileEnabled"] = "true",
            ["Serilog:Json:Formatter"] = "RenderedCompact"
        });

        var formatter = DefaultVapeCacheJsonLogFormatterResolver.Instance
            .ResolveFormatter(configuration, VapeCacheJsonSinkTarget.File);

        Assert.IsType<RenderedCompactJsonFormatter>(formatter);
    }

    [Fact]
    public void DefaultResolver_ReturnsJsonFormatter_WhenConfigured()
    {
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("resolver-json"), new Dictionary<string, string?>
        {
            ["Serilog:Json:Enabled"] = "true",
            ["Serilog:Json:FileEnabled"] = "true",
            ["Serilog:Json:Formatter"] = "Json",
            ["Serilog:Json:RenderMessage"] = "true"
        });

        var formatter = DefaultVapeCacheJsonLogFormatterResolver.Instance
            .ResolveFormatter(configuration, VapeCacheJsonSinkTarget.File);

        Assert.IsType<JsonFormatter>(formatter);
    }

    [Fact]
    public void DefaultResolver_ReturnsNull_WhenConsoleTargetNotEnabled()
    {
        var configuration = BuildFileOnlyConfig(CreateTempLogPath("resolver-console"), new Dictionary<string, string?>
        {
            ["Serilog:Json:Enabled"] = "true",
            ["Serilog:Json:ConsoleEnabled"] = "false"
        });

        var formatter = DefaultVapeCacheJsonLogFormatterResolver.Instance
            .ResolveFormatter(configuration, VapeCacheJsonSinkTarget.Console);

        Assert.Null(formatter);
    }

    [Fact]
    public void ConfigureVapeCacheLogging_DevelopmentDefault_AllowsInformationForFileSink()
    {
        var logFile = Path.Combine(Path.GetTempPath(), $"vapecache-log-dev-{Guid.NewGuid():N}.log");
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var configuration = BuildFileOnlyConfig(logFile);

            using (var logger = new LoggerConfiguration()
                       .ConfigureVapeCacheLogging(configuration, services, environmentName: "Development")
                       .CreateLogger())
            {
                logger.Information("development-info-should-be-written");
            }

            var content = ReadAllTextEventually(logFile, TimeSpan.FromSeconds(5));
            Assert.Contains("development-info-should-be-written", content, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(logFile);
        }
    }

    [Fact]
    public void ConfigureVapeCacheLogging_JsonEnabled_WritesCompactJsonToFile()
    {
        var logFile = Path.Combine(Path.GetTempPath(), $"vapecache-log-json-{Guid.NewGuid():N}.log");
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var configuration = BuildFileOnlyConfig(logFile, new Dictionary<string, string?>
            {
                ["Serilog:Json:Enabled"] = "true",
                ["Serilog:Json:FileEnabled"] = "true"
            });

            using (var logger = new LoggerConfiguration()
                       .ConfigureVapeCacheLogging(configuration, services, environmentName: "Development")
                       .CreateLogger())
            {
                logger.Information("json-info {Id}", 7);
            }

            var content = ReadAllTextEventually(logFile, TimeSpan.FromSeconds(5));
            Assert.Contains("\"@mt\":\"json-info {Id}\"", content, StringComparison.Ordinal);
            Assert.Contains("\"Id\":7", content, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(logFile);
        }
    }

    [Fact]
    public void ConfigureVapeCacheLogging_CustomJsonFormatterResolver_CanOverrideDefaultBehavior()
    {
        var logFile = Path.Combine(Path.GetTempPath(), $"vapecache-log-custom-json-{Guid.NewGuid():N}.log");
        try
        {
            using var services = new ServiceCollection()
                .AddSingleton<IVapeCacheJsonLogFormatterResolver, AlwaysJsonFileFormatterResolver>()
                .BuildServiceProvider();

            var configuration = BuildFileOnlyConfig(logFile);

            using (var logger = new LoggerConfiguration()
                       .ConfigureVapeCacheLogging(configuration, services, environmentName: "Development")
                       .CreateLogger())
            {
                logger.Information("custom-json {Value}", 12);
            }

            var content = ReadAllTextEventually(logFile, TimeSpan.FromSeconds(5));
            Assert.Contains("\"@mt\":\"custom-json {Value}\"", content, StringComparison.Ordinal);
            Assert.Contains("\"Value\":12", content, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(logFile);
        }
    }

    private static IConfiguration BuildFileOnlyConfig(string logFile, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Serilog:FallbackConsole:Enabled"] = "false",
            ["Serilog:Seq:Enabled"] = "false",
            ["Serilog:OpenTelemetry:Enabled"] = "false",
            ["Serilog:File:Enabled"] = "true",
            ["Serilog:File:Path"] = logFile,
            ["Serilog:File:RollingInterval"] = "Infinite",
            ["Serilog:File:FlushToDiskIntervalSeconds"] = "1"
        };

        if (overrides is not null)
        {
            foreach (var pair in overrides)
                values[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static string ReadAllTextEventually(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastException = null;

        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException ex)
            {
                lastException = ex;
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException($"Timed out waiting to read log file '{path}'.", lastException);
    }

    private sealed class AlwaysJsonFileFormatterResolver : IVapeCacheJsonLogFormatterResolver
    {
        public ITextFormatter? ResolveFormatter(IConfiguration configuration, VapeCacheJsonSinkTarget target)
        {
            return target == VapeCacheJsonSinkTarget.File
                ? new CompactJsonFormatter()
                : null;
        }
    }

    private static string CreateTempLogPath(string prefix)
        => Path.Combine(Path.GetTempPath(), $"vapecache-{prefix}-{Guid.NewGuid():N}.log");
}
