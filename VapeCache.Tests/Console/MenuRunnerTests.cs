using System.Reflection;
using Microsoft.Extensions.Configuration;
using VapeCache.Console.GroceryStore;

namespace VapeCache.Tests.Console;

[Collection(ConsoleIoCollection.Name)]
public sealed class MenuRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_cleanly_when_user_selects_exit()
    {
        var previousIn = System.Console.In;
        var previousOut = System.Console.Out;
        var previousEnv = Environment.GetEnvironmentVariable("VAPECACHE_RUN_COMPARISON");

        try
        {
            System.Console.SetIn(new StringReader("0" + Environment.NewLine));
            using var output = new StringWriter();
            System.Console.SetOut(output);
            Environment.SetEnvironmentVariable("VAPECACHE_RUN_COMPARISON", null);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RedisConnection:Host"] = "127.0.0.1",
                    ["RedisConnection:Password"] = "test"
                })
                .Build();

            await MenuRunner.RunAsync(config);

            var text = output.ToString();
            Assert.Contains("Enter shopper count to run comparison:", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAPECACHE_RUN_COMPARISON", previousEnv);
            System.Console.SetIn(previousIn);
            System.Console.SetOut(previousOut);
        }
    }

    [Fact]
    public async Task RunAsync_prefers_connection_string_for_endpoint_and_auth()
    {
        var previousIn = System.Console.In;
        var previousOut = System.Console.Out;
        var previousEnv = Environment.GetEnvironmentVariable("VAPECACHE_RUN_COMPARISON");

        try
        {
            System.Console.SetIn(new StringReader("0" + Environment.NewLine));
            using var output = new StringWriter();
            System.Console.SetOut(output);
            Environment.SetEnvironmentVariable("VAPECACHE_RUN_COMPARISON", null);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RedisConnection:ConnectionString"] = "redis://bench-user:bench-pass@10.20.30.40:6380/0",
                    ["RedisConnection:Host"] = "127.0.0.1",
                    ["RedisConnection:Port"] = "6379",
                    ["RedisConnection:Username"] = "",
                    ["RedisConnection:Password"] = ""
                })
                .Build();

            await MenuRunner.RunAsync(config);

            var text = output.ToString();
            Assert.Contains("Redis Endpoint: 10.20.30.40:6380", text);
            Assert.Contains("Redis Auth: acl", text);
            Assert.Contains("Redis Source: connection-string", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAPECACHE_RUN_COMPARISON", previousEnv);
            System.Console.SetIn(previousIn);
            System.Console.SetOut(previousOut);
        }
    }

    [Fact]
    public void GetCustomShopperCount_returns_default_for_invalid_input()
    {
        var previousIn = System.Console.In;
        var previousOut = System.Console.Out;

        try
        {
            System.Console.SetIn(new StringReader("abc" + Environment.NewLine));
            using var output = new StringWriter();
            System.Console.SetOut(output);

            var result = InvokeGetCustomShopperCount();

            Assert.Equal(10_000, result);
            Assert.Contains("Invalid input. Using default: 10,000", output.ToString());
        }
        finally
        {
            System.Console.SetIn(previousIn);
            System.Console.SetOut(previousOut);
        }
    }

    [Fact]
    public void GetCustomShopperCount_returns_user_value_for_valid_input()
    {
        var previousIn = System.Console.In;
        var previousOut = System.Console.Out;

        try
        {
            System.Console.SetIn(new StringReader("12345" + Environment.NewLine));
            using var output = new StringWriter();
            System.Console.SetOut(output);

            var result = InvokeGetCustomShopperCount();

            Assert.Equal(12_345, result);
        }
        finally
        {
            System.Console.SetIn(previousIn);
            System.Console.SetOut(previousOut);
        }
    }

    private static int InvokeGetCustomShopperCount()
    {
        var method = typeof(MenuRunner).GetMethod("GetCustomShopperCount", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (int)method!.Invoke(null, null)!;
    }
}
