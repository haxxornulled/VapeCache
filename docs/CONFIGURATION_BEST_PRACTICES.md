# VapeCache Configuration Best Practices

## Core Principle: Host Owns Configuration

**Libraries should NEVER touch `IConfiguration` directly.** The host project (Program.cs) owns configuration and binds it to `IOptions<T>`. Libraries receive configured options via dependency injection.

## Current Architecture ✅

### VapeCache.Infrastructure (Library)
```csharp
// ✅ CORRECT: Library uses IServiceCollection extension methods
public static IServiceCollection AddVapecacheRedisConnections(
    this IServiceCollection services)
{
    // Register services - does NOT touch IConfiguration
    services.AddSingleton<IRedisConnectionFactory, RedisConnectionFactory>();
    services.AddSingleton<RedisConnectionPool>();
    return services;
}

// ✅ CORRECT: Services receive IOptions<T> via DI
internal sealed class RedisConnectionFactory(
    IOptionsMonitor<RedisConnectionOptions> options,  // ← Configured by host
    ILogger<RedisConnectionFactory> logger)
{
    // Use options.CurrentValue to get configuration
}
```

### VapeCache.Console (Host)
```csharp
// ✅ CORRECT: Host owns IConfiguration and binds to options
var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        // Host controls configuration sources
        config.AddJsonFile("appsettings.json");
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        // Host binds configuration to options
        services
            .AddOptions<RedisConnectionOptions>()
            .Bind(context.Configuration.GetSection("RedisConnection"))
            .ValidateOnStart();

        // Library registers services (no IConfiguration access)
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();
    });
```

## Why This Matters

### ❌ Bad: Library Touches IConfiguration
```csharp
// WRONG: Library should NOT do this
public static IServiceCollection AddVapeCache(
    this IServiceCollection services,
    IConfiguration configuration)  // ← BAD: Library shouldn't need this
{
    var connectionString = configuration["RedisConnection:ConnectionString"];
    services.AddSingleton(new RedisConnectionOptions
    {
        ConnectionString = connectionString
    });
    return services;
}
```

**Problems:**
- Forces specific configuration structure on users
- Breaks when users have different config sources (KeyVault, environment, etc.)
- Library dictates configuration, not the host
- Hard to test (need to mock IConfiguration)

### ✅ Good: Host Binds Configuration
```csharp
// CORRECT: Library exposes IServiceCollection extension
public static IServiceCollection AddVapecacheRedisConnections(
    this IServiceCollection services)
{
    // Just register services - host provides configuration
    services.AddSingleton<IRedisConnectionFactory, RedisConnectionFactory>();
    return services;
}

// Host binds configuration to options
services
    .AddOptions<RedisConnectionOptions>()
    .Bind(configuration.GetSection("RedisConnection"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Host is required")
    .ValidateOnStart();

services.AddVapecacheRedisConnections();  // ← No configuration passed!
```

**Benefits:**
- Host controls configuration sources (appsettings.json, env vars, KeyVault, etc.)
- Library is configuration-agnostic
- Users can override configuration in tests
- Follows .NET best practices
- Compatible with `IOptionsMonitor<T>` for hot-reload

## IOptions Pattern

VapeCache uses the `IOptions<T>` pattern for all configuration:

```csharp
// Options classes are plain POCOs
public sealed record RedisConnectionOptions
{
    public string ConnectionString { get; init; } = "";
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 6379;
    // ... more properties
}

// Services receive IOptions<T> or IOptionsMonitor<T>
internal sealed class RedisConnectionFactory(
    IOptionsMonitor<RedisConnectionOptions> options)  // ← Hot-reload support
{
    public async ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;  // ← Get current config
        await ConnectAsync(o.Host, o.Port, ct);
    }
}
```

### IOptions vs IOptionsMonitor vs IOptionsSnapshot

| Interface | Lifetime | Hot-Reload | Use Case |
|-----------|----------|------------|----------|
| `IOptions<T>` | Singleton | ❌ No | Static config (read once) |
| `IOptionsMonitor<T>` | Singleton | ✅ Yes | Config can change at runtime |
| `IOptionsSnapshot<T>` | Scoped | ✅ Yes | Per-request config (ASP.NET Core) |

**VapeCache uses `IOptionsMonitor<T>`** because Redis connection settings can change at runtime (e.g., failover to different endpoint).

## .NET Aspire Integration Pattern

### ❌ Wrong: Aspire Extension Reads IConfiguration
```csharp
// WRONG: Don't do this!
public static class AspireVapeCacheExtensions
{
    public static AspireVapeCacheBuilder AddVapeCache(
        this IHostApplicationBuilder builder)
    {
        // ❌ BAD: Extension shouldn't read configuration directly
        var connectionString = builder.Configuration.GetConnectionString("redis");

        builder.Services.Configure<RedisConnectionOptions>(options =>
        {
            options.ConnectionString = connectionString;
        });

        return new AspireVapeCacheBuilder(builder);
    }
}
```

**Problem:** Aspire extension is reading configuration, not the host. This violates separation of concerns.

### ✅ Correct: Extension Enables Configuration, Host Provides It
```csharp
// CORRECT: Extension sets up services, host provides config
public static class AspireVapeCacheExtensions
{
    public static AspireVapeCacheBuilder AddVapeCache(
        this IHostApplicationBuilder builder)
    {
        // ✅ Just register services - don't read configuration
        builder.Services.AddVapecacheRedisConnections();
        builder.Services.AddVapecacheCaching();

        return new AspireVapeCacheBuilder(builder);
    }

    public static AspireVapeCacheBuilder WithRedisFromAspire(
        this AspireVapeCacheBuilder builder,
        string connectionName)
    {
        // ✅ Use Aspire's built-in service discovery binding
        // This configures RedisConnectionOptions from Aspire resource
        builder.Builder.AddRedisClient(connectionName, settings =>
        {
            // Aspire handles the configuration binding automatically
            // We just map Aspire's config to VapeCache's options
        });

        return builder;
    }
}

// Host usage - configuration is implicit via Aspire resource binding
var builder = WebApplication.CreateBuilder(args);

builder.AddVapeCache()
    .WithRedisFromAspire("redis");  // ← Aspire injects config via service discovery
```

**Better:** Let Aspire's service discovery handle configuration injection. The extension just enables the binding.

## Testing Strategy

### Unit Tests: Override Configuration
```csharp
[Fact]
public async Task RedisConnectionFactory_UsesConfiguredHost()
{
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // ✅ Tests control configuration via IOptions
    services.Configure<RedisConnectionOptions>(options =>
    {
        options.Host = "test.redis.local";
        options.Port = 6380;
    });

    services.AddVapecacheRedisConnections();

    var provider = services.BuildServiceProvider();
    var factory = provider.GetRequiredService<IRedisConnectionFactory>();

    // Act & Assert
    // Factory uses test configuration, not appsettings.json
}
```

### Integration Tests: Use Test Configuration
```csharp
[Fact]
public async Task VapeCache_WorksWithTestRedis()
{
    // Arrange
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RedisConnection:Host"] = "localhost",
            ["RedisConnection:Port"] = "6379"
        })
        .Build();

    var services = new ServiceCollection();
    services.AddLogging();

    // ✅ Bind test configuration to options
    services
        .AddOptions<RedisConnectionOptions>()
        .Bind(config.GetSection("RedisConnection"));

    services.AddVapecacheRedisConnections();

    var provider = services.BuildServiceProvider();
    var factory = provider.GetRequiredService<IRedisConnectionFactory>();

    // Act
    var result = await factory.CreateAsync(default);

    // Assert
    Assert.True(result.IsSuccess);
}
```

## Configuration Sources Priority

Host projects control configuration source priority:

```csharp
var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        // Priority (last wins):
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true);  // 1. Base config
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);  // 2. Environment-specific
        config.AddEnvironmentVariables();  // 3. Environment variables (override JSON)
        config.AddCommandLine(args);  // 4. Command-line args (highest priority)

        // Example: KeyVault for production secrets
        if (context.HostingEnvironment.IsProduction())
        {
            var builtConfig = config.Build();
            var keyVaultUrl = builtConfig["KeyVault:Url"];
            if (!string.IsNullOrEmpty(keyVaultUrl))
            {
                config.AddAzureKeyVault(new Uri(keyVaultUrl), new DefaultAzureCredential());
            }
        }
    });
```

**VapeCache.Infrastructure doesn't know or care** where configuration comes from - it just receives `IOptions<T>`.

## Real-World Example: Multi-Environment Setup

### appsettings.json (Development)
```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "UseTls": false
  }
}
```

### appsettings.Production.json
```json
{
  "RedisConnection": {
    "Host": "redis.prod.internal",
    "Port": 6380,
    "UseTls": true,
    "TlsHost": "redis.prod.internal"
  }
}
```

### Environment Variables (Kubernetes/Docker)
```bash
# Override connection string entirely
export RedisConnection__ConnectionString="rediss://user:pass@redis.prod.internal:6380/0"

# Or override individual properties (double underscore for nested keys)
export RedisConnection__Host="redis.k8s.svc.cluster.local"
export RedisConnection__Port="6379"
```

### Azure KeyVault (Production Secrets)
```csharp
// Host adds KeyVault as config source
config.AddAzureKeyVault(new Uri("https://myvault.vault.azure.net/"), new DefaultAzureCredential());

// KeyVault secrets map to configuration keys:
// Secret: RedisConnection--Password
// Maps to: RedisConnection:Password in IConfiguration
```

**VapeCache.Infrastructure just works** with all of these - it receives `IOptions<RedisConnectionOptions>` regardless of source.

## Summary: Separation of Concerns

| Responsibility | Owner | Mechanism |
|----------------|-------|-----------|
| **Define configuration schema** | Library (VapeCache.Infrastructure) | `RedisConnectionOptions` POCO |
| **Register services** | Library | `AddVapecacheRedisConnections()` extension |
| **Provide configuration** | Host (Program.cs) | `IConfiguration` → `IOptions<T>` binding |
| **Choose config sources** | Host | `ConfigureAppConfiguration()` |
| **Validate configuration** | Host | `.Validate()` in options builder |
| **Consume configuration** | Library services | `IOptionsMonitor<T>` dependency injection |

## Checklist: Library Configuration Best Practices

✅ **VapeCache.Infrastructure Compliance:**

- [x] Uses `IServiceCollection` extension methods (not `IConfiguration` parameters)
- [x] Defines options classes as POCOs (`RedisConnectionOptions`, etc.)
- [x] Services receive `IOptions<T>` or `IOptionsMonitor<T>` via DI
- [x] No direct `IConfiguration` access in library code
- [x] No `ConfigurationManager` or `IConfigurationRoot` dependencies
- [x] Configuration validation is optional (host chooses to validate)
- [x] Works with any configuration source (JSON, env vars, KeyVault, etc.)
- [x] Testable without real configuration files

## References

- [Options pattern in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options)
- [Configuration in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration)
- [Dependency injection best practices](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines)
- [.NET Aspire service defaults](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/service-defaults)
