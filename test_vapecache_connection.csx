#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Extensions.Logging.Console, 9.0.0"
#r "nuget: Microsoft.Extensions.Options, 9.0.0"

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

Console.WriteLine("VapeCache Connection Test");
Console.WriteLine("=========================\n");

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

var options = new RedisConnectionOptions
{
    Host = "192.168.100.50",
    Port = 6379,
    Username = "dfw",
    Password = "dfw4me",
    ConnectTimeout = TimeSpan.FromSeconds(10)
};

Console.WriteLine($"Connecting to {options.Host}:{options.Port}...");
Console.WriteLine($"Username: {options.Username}");
Console.WriteLine($"Timeout: {options.ConnectTimeout}\n");

var monitor = new TestOptionsMonitor(options);
var logger = loggerFactory.CreateLogger<RedisConnectionFactory>();
var factory = new RedisConnectionFactory(monitor, logger, Array.Empty<IRedisConnectionObserver>());

try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = await factory.CreateAsync(cts.Token);

    result.Match(
        conn =>
        {
            Console.WriteLine("✓ Connection successful!");
            conn.Dispose();
            return 0;
        },
        ex =>
        {
            Console.WriteLine($"✗ Connection failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        });
}
catch (Exception ex)
{
    Console.WriteLine($"✗ Unhandled exception: {ex}");
}

class TestOptionsMonitor : IOptionsMonitor<RedisConnectionOptions>
{
    private readonly RedisConnectionOptions _value;
    public TestOptionsMonitor(RedisConnectionOptions value) => _value = value;
    public RedisConnectionOptions CurrentValue => _value;
    public RedisConnectionOptions Get(string? name) => _value;
    public IDisposable? OnChange(Action<RedisConnectionOptions, string?> listener) => null;
}
