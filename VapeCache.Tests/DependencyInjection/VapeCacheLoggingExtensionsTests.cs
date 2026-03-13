using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VapeCache.Extensions.Logging;

namespace VapeCache.Tests.DependencyInjection;

public sealed class VapeCacheLoggingExtensionsTests
{
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

    private static IConfiguration BuildFileOnlyConfig(string logFile)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:FallbackConsole:Enabled"] = "false",
                ["Serilog:Seq:Enabled"] = "false",
                ["Serilog:OpenTelemetry:Enabled"] = "false",
                ["Serilog:File:Enabled"] = "true",
                ["Serilog:File:Path"] = logFile,
                ["Serilog:File:RollingInterval"] = "Infinite",
                ["Serilog:File:FlushToDiskIntervalSeconds"] = "1"
            })
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
}
