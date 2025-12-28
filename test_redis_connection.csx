#!/usr/bin/env dotnet-script
#r "nuget: StackExchange.Redis, 2.8.16"

using StackExchange.Redis;
using System;
using System.Diagnostics;

var host = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_HOST") ?? "192.168.100.50";
var port = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_PORT") ?? "6379";
var username = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_USERNAME") ?? "admin";
var password = Environment.GetEnvironmentVariable("VAPECACHE_REDIS_PASSWORD") ?? "fuckoff!";
var database = int.Parse(Environment.GetEnvironmentVariable("VAPECACHE_REDIS_DATABASE") ?? "0");

Console.WriteLine($"Testing Redis connection to {host}:{port}");
Console.WriteLine($"Username: {username}");
Console.WriteLine($"Database: {database}");

var config = new ConfigurationOptions
{
    EndPoints = { { host, int.Parse(port) } },
    User = username,
    Password = password,
    DefaultDatabase = database,
    ConnectTimeout = 5000,
    SyncTimeout = 5000,
    AbortOnConnectFail = false
};

var sw = Stopwatch.StartNew();
try
{
    Console.WriteLine("Connecting...");
    using var connection = ConnectionMultiplexer.Connect(config);
    Console.WriteLine($"✅ Connected in {sw.ElapsedMilliseconds}ms");

    var db = connection.GetDatabase();
    var testKey = "test:" + Guid.NewGuid();

    Console.WriteLine($"Setting key {testKey}...");
    await db.StringSetAsync(testKey, "test-value");
    Console.WriteLine("✅ SET successful");

    Console.WriteLine($"Getting key {testKey}...");
    var value = await db.StringGetAsync(testKey);
    Console.WriteLine($"✅ GET successful: {value}");

    Console.WriteLine($"Deleting key {testKey}...");
    await db.KeyDeleteAsync(testKey);
    Console.WriteLine("✅ DELETE successful");

    Console.WriteLine($"\n✅ ALL TESTS PASSED in {sw.ElapsedMilliseconds}ms");
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ ERROR after {sw.ElapsedMilliseconds}ms:");
    Console.WriteLine($"Type: {ex.GetType().Name}");
    Console.WriteLine($"Message: {ex.Message}");
    if (ex.InnerException != null)
    {
        Console.WriteLine($"Inner: {ex.InnerException.Message}");
    }
    Console.WriteLine($"\nStack trace:\n{ex.StackTrace}");
}
