# VapeCache Configuration Guide

Complete reference for configuring VapeCache via `appsettings.json`, environment variables, and programmatic options.

## Table of Contents

- [Overview](#overview)
- [Connection Configuration](#connection-configuration)
- [Cache Configuration](#cache-configuration)
- [Circuit Breaker Configuration](#circuit-breaker-configuration)
- [Connection Pool Configuration](#connection-pool-configuration)
- [Observability Configuration](#observability-configuration)
- [Environment Variables](#environment-variables)
- [Programmatic Configuration](#programmatic-configuration)
- [Configuration Best Practices](#configuration-best-practices)

---

## Overview

VapeCache uses the **IOptions<T> pattern** for configuration. This means:

1. **Host owns configuration** (`Program.cs` binds `IConfiguration` to POCOs)
2. **Library receives options** (via `IOptionsMonitor<T>` dependency injection)
3. **Configuration is testable** (override options in tests without appsettings.json)

See [CONFIGURATION_BEST_PRACTICES.md](CONFIGURATION_BEST_PRACTICES.md) for architectural rationale.

---

## Connection Configuration

### RedisConnectionOptions

Controls how VapeCache connects to Redis servers.

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Username": null,
    "Password": null,
    "Database": 0,
    "UseTls": false,
    "AllowInvalidCert": false,
    "TlsHost": null,
    "ConnectTimeoutMs": 5000,
    "SocketConnectTimeoutMs": 5000,
    "SocketSendTimeoutMs": 5000,
    "SocketReceiveTimeoutMs": 5000
  }
}
```

### Connection Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `Host` | string | `"localhost"` | Redis server hostname or IP address |
| `Port` | int | `6379` | Redis server port (use `6380` for TLS) |
| `Username` | string? | `null` | Redis 6+ ACL username (optional) |
| `Password` | string? | `null` | Redis password (AUTH command) |
| `Database` | int | `0` | Redis database number (0-15) |
| `UseTls` | bool | `false` | Enable TLS/SSL encryption |
| `AllowInvalidCert` | bool | `false` | Skip certificate validation (DEV ONLY) |
| `TlsHost` | string? | `null` | SNI hostname for TLS (overrides `Host`) |

**Security Note:** `AllowInvalidCert=true` is **blocked in production environments** to prevent MITM attacks. Use proper CA-signed certificates or set `ASPNETCORE_ENVIRONMENT=Development`.

### Timeout Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `ConnectTimeoutMs` | int | `5000` | Timeout for initial connection establishment |
| `SocketConnectTimeoutMs` | int | `5000` | TCP socket connect timeout |
| `SocketSendTimeoutMs` | int | `5000` | Socket send operation timeout |
| `SocketReceiveTimeoutMs` | int | `5000` | Socket receive operation timeout |

**Tuning Guidance:**
- **Low latency networks** (same datacenter): `1000-2000ms`
- **High latency networks** (cross-region): `5000-10000ms`
- **Startup preflight**: Use `ConnectTimeoutMs` to fail fast if Redis is unavailable

---

## Cache Configuration

### CacheServiceOptions

Controls hybrid cache behavior, circuit breaker, and in-memory fallback.

```json
{
  "CacheService": {
    "EnableCircuitBreaker": true,
    "CircuitBreakerFailureThreshold": 5,
    "CircuitBreakerSamplingDurationSeconds": 30,
    "CircuitBreakerBreakDurationSeconds": 60,
    "InMemoryCacheSizeLimitMb": 100,
    "InMemoryCacheExpirationScanFrequencyMinutes": 5
  }
}
```

### Cache Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `EnableCircuitBreaker` | bool | `true` | Enable circuit breaker for Redis failures |
| `CircuitBreakerFailureThreshold` | int | `5` | Failures before circuit opens |
| `CircuitBreakerSamplingDurationSeconds` | int | `30` | Time window for failure counting |
| `CircuitBreakerBreakDurationSeconds` | int | `60` | How long circuit stays open before half-open |
| `InMemoryCacheSizeLimitMb` | int | `100` | Max memory for fallback cache (MB) |
| `InMemoryCacheExpirationScanFrequencyMinutes` | int | `5` | How often to scan for expired entries |

**Circuit Breaker Example:**
- If **5 failures** occur within **30 seconds**, circuit opens
- Circuit stays open for **60 seconds** (fallback to in-memory)
- After 60 seconds, circuit enters **half-open** (sanity check Redis)
- If sanity check succeeds, circuit closes (resume Redis)
- If sanity check fails, circuit reopens (wait another 60 seconds)

---

## Connection Pool Configuration

### RedisConnectionPoolOptions

Controls connection pooling behavior (multiplexed connections, warm-up, reaper).

```json
{
  "RedisConnectionPool": {
    "MinPoolSize": 2,
    "MaxPoolSize": 10,
    "AcquireTimeoutMs": 5000,
    "IdleTimeoutMs": 300000,
    "ValidationTimeoutMs": 1000,
    "MaxInFlightPerConnection": 4096,
    "ReaperIntervalMs": 60000,
    "WarmPoolOnStartup": true
  }
}
```

### Pool Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `MinPoolSize` | int | `2` | Minimum number of connections to maintain |
| `MaxPoolSize` | int | `10` | Maximum number of connections allowed |
| `AcquireTimeoutMs` | int | `5000` | Timeout for acquiring connection from pool |
| `IdleTimeoutMs` | int | `300000` | Idle time before connection is reaped (5 min) |
| `ValidationTimeoutMs` | int | `1000` | Timeout for connection validation (PING) |
| `MaxInFlightPerConnection` | int | `4096` | Max concurrent commands per connection |
| `ReaperIntervalMs` | int | `60000` | How often to run idle connection reaper (1 min) |
| `WarmPoolOnStartup` | bool | `true` | Pre-create `MinPoolSize` connections at startup |

**Tuning Guidance:**
- **Low concurrency** (< 100 req/s): `MinPoolSize=2, MaxPoolSize=4`
- **Medium concurrency** (100-1000 req/s): `MinPoolSize=4, MaxPoolSize=10`
- **High concurrency** (> 1000 req/s): `MinPoolSize=8, MaxPoolSize=20`
- **Startup preflight**: Set `WarmPoolOnStartup=true` to validate Redis before serving traffic

---

## Observability Configuration

### Metrics (OpenTelemetry)

VapeCache exports metrics via `Meter` (name: `VapeCache.Redis`).

```csharp
// Program.cs
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("VapeCache.Redis"); // Enable VapeCache metrics
        metrics.AddPrometheusExporter();     // Export to Prometheus
    });
```

**Available Metrics:**
- `redis.connect.attempts` (Counter) - Connection attempts
- `redis.connect.failures` (Counter) - Connection failures
- `redis.connect.ms` (Histogram) - Connection duration
- `redis.pool.acquires` (Counter) - Pool acquisitions
- `redis.pool.timeouts` (Counter) - Pool acquisition timeouts
- `redis.pool.wait.ms` (Histogram) - Pool wait time
- `redis.pool.drops` (Counter) - Dropped connections
- `redis.cmd.calls` (Counter) - Redis commands executed
- `redis.cmd.failures` (Counter) - Command failures
- `redis.cmd.ms` (Histogram) - Command duration
- `redis.bytes.sent` (Counter) - Bytes sent to Redis
- `redis.bytes.received` (Counter) - Bytes received from Redis

### Distributed Tracing (OpenTelemetry)

VapeCache creates `Activity` spans for every Redis operation.

```csharp
// Program.cs
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("VapeCache.Redis"); // Enable VapeCache traces
        tracing.AddOtlpExporter();            // Export to Jaeger/Zipkin
    });
```

**Trace Hierarchy:**
```
GET user:123 (parent span)
  ├─ redis.pool.acquire (acquire connection)
  ├─ redis.cmd.get (execute GET command)
  └─ redis.pool.release (release connection)
```

### Structured Logging

VapeCache uses `ILogger<T>` abstraction (works with Serilog, NLog, etc.).

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "VapeCache.Infrastructure": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

**Example: Serilog + SEQ**
```json
{
  "Serilog": {
    "Using": ["Serilog.Sinks.Console", "Serilog.Sinks.Seq"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "VapeCache.Infrastructure": "Information"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "Seq", "Args": { "serverUrl": "http://localhost:5341" } }
    ],
    "Enrich": ["FromLogContext"]
  }
}
```

See [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) for comprehensive observability guide.

---

## Environment Variables

VapeCache supports environment variable overrides for secrets and deployment-specific settings.

### Connection String Override

**Environment Variable:** `VAPECACHE_REDIS_CONNECTIONSTRING`

**Format:** `redis://[username:password@]host:port[/database][?useTls=true]`

**Examples:**
```bash
# Development (localhost, no auth)
export VAPECACHE_REDIS_CONNECTIONSTRING="redis://localhost:6379/0"

# Production (TLS + password)
export VAPECACHE_REDIS_CONNECTIONSTRING="redis://:mypassword@redis.example.com:6380/0?useTls=true"

# Redis 6+ ACL (username + password)
export VAPECACHE_REDIS_CONNECTIONSTRING="redis://admin:secretpass@redis.example.com:6379/0"
```

### Individual Parameter Overrides

You can override any `appsettings.json` value via environment variables using the **colon `:` separator** (or double underscore `__` on some platforms).

**Examples:**
```bash
# Override Redis host
export RedisConnection__Host="prod-redis.example.com"

# Override Redis password
export RedisConnection__Password="secret-from-keyvault"

# Override pool size
export RedisConnectionPool__MaxPoolSize="20"

# Override circuit breaker threshold
export CacheService__CircuitBreakerFailureThreshold="10"
```

**Azure App Service / Kubernetes:**
```yaml
env:
  - name: RedisConnection__Host
    value: "redis.default.svc.cluster.local"
  - name: RedisConnection__Password
    valueFrom:
      secretKeyRef:
        name: redis-secret
        key: password
```

---

## Programmatic Configuration

You can override configuration programmatically using `Configure<T>` or `PostConfigure<T>`.

### Example: Override Pool Size in Code

```csharp
// Program.cs
builder.Services.Configure<RedisConnectionPoolOptions>(options =>
{
    options.MaxPoolSize = 20; // Override appsettings.json
});
```

### Example: Environment-Specific Configuration

```csharp
// Program.cs
if (builder.Environment.IsProduction())
{
    builder.Services.Configure<CacheServiceOptions>(options =>
    {
        options.CircuitBreakerFailureThreshold = 10; // More lenient in prod
    });
}
```

### Example: Azure Key Vault Integration

VapeCache supports loading Redis credentials from Azure Key Vault using the `ConnectionString` property. This is the recommended approach for production deployments to avoid hardcoding secrets in appsettings.json.

#### NuGet Packages Required

```bash
dotnet add package Azure.Extensions.AspNetCore.Configuration.Secrets
dotnet add package Azure.Identity
```

#### Option 1: Load Connection String from Key Vault (Recommended)

Store the entire Redis connection string in Key Vault as a single secret.

**Step 1: Create Key Vault Secret**

```bash
# Using Azure CLI
az keyvault secret set \
  --vault-name "myvault" \
  --name "RedisConnectionString" \
  --value "redis://:YOUR_PASSWORD@redis.example.com:6380/0?useTls=true"
```

**Step 2: Configure Program.cs**

```csharp
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add Azure Key Vault to configuration
builder.Configuration.AddAzureKeyVault(
    new Uri("https://myvault.vault.azure.net/"),
    new DefaultAzureCredential());

// Add VapeCache services (will read ConnectionString from Key Vault)
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

var app = builder.Build();
app.Run();
```

**Step 3: Update appsettings.json**

```json
{
  "RedisConnection": {
    "ConnectionString": null  // Will be loaded from Key Vault secret "RedisConnectionString"
  }
}
```

**How It Works:**
- `AddAzureKeyVault()` maps Key Vault secrets to `IConfiguration` keys
- Secret `RedisConnectionString` → `IConfiguration["RedisConnection:ConnectionString"]`
- VapeCache parses the connection string and overrides individual properties (Host, Port, Password, UseTls, etc.)

#### Option 2: Load Individual Secrets from Key Vault

Store individual connection parameters as separate Key Vault secrets.

**Step 1: Create Key Vault Secrets**

```bash
az keyvault secret set --vault-name "myvault" --name "RedisConnection--Host" --value "redis.example.com"
az keyvault secret set --vault-name "myvault" --name "RedisConnection--Port" --value "6380"
az keyvault secret set --vault-name "myvault" --name "RedisConnection--Password" --value "YOUR_PASSWORD"
az keyvault secret set --vault-name "myvault" --name "RedisConnection--UseTls" --value "true"
```

**Step 2: Configure Program.cs**

```csharp
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add Azure Key Vault to configuration
builder.Configuration.AddAzureKeyVault(
    new Uri("https://myvault.vault.azure.net/"),
    new DefaultAzureCredential());

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

var app = builder.Build();
app.Run();
```

**Step 3: Update appsettings.json** (minimal/optional)

```json
{
  "RedisConnection": {
    "Database": 0  // Only non-secret defaults
  }
}
```

**How It Works:**
- `AddAzureKeyVault()` replaces `--` (double dash) with `:` (colon)
- Secret `RedisConnection--Password` → `IConfiguration["RedisConnection:Password"]`
- Individual properties override appsettings.json defaults

#### Option 3: Managed Identity (Production Recommended)

Use Azure Managed Identity to access Key Vault without storing credentials.

**Step 1: Enable Managed Identity**

```bash
# Enable system-assigned managed identity on Azure App Service
az webapp identity assign --name myapp --resource-group mygroup

# Grant Key Vault access to the managed identity
az keyvault set-policy \
  --name myvault \
  --object-id <MANAGED_IDENTITY_OBJECT_ID> \
  --secret-permissions get list
```

**Step 2: Configure Program.cs**

```csharp
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// DefaultAzureCredential automatically uses Managed Identity in Azure
builder.Configuration.AddAzureKeyVault(
    new Uri("https://myvault.vault.azure.net/"),
    new DefaultAzureCredential());

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

var app = builder.Build();
app.Run();
```

**Benefits:**
- No credentials in code or configuration
- Automatic credential rotation
- Works across Azure services (App Service, Container Apps, AKS, Functions)

#### Option 4: Client Secret (Development/Testing)

Use a service principal with client secret for local development.

**Step 1: Create Service Principal**

```bash
az ad sp create-for-rbac --name "vapecache-dev-sp" --skip-assignment
# Output: appId, password, tenant

# Grant Key Vault access
az keyvault set-policy \
  --name myvault \
  --spn <APP_ID> \
  --secret-permissions get list
```

**Step 2: Configure Program.cs**

```csharp
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Use ClientSecretCredential for local development
if (builder.Environment.IsDevelopment())
{
    var tenantId = builder.Configuration["AzureAd:TenantId"];
    var clientId = builder.Configuration["AzureAd:ClientId"];
    var clientSecret = builder.Configuration["AzureAd:ClientSecret"];

    builder.Configuration.AddAzureKeyVault(
        new Uri("https://myvault.vault.azure.net/"),
        new ClientSecretCredential(tenantId, clientId, clientSecret));
}
else
{
    // Production: Use Managed Identity
    builder.Configuration.AddAzureKeyVault(
        new Uri("https://myvault.vault.azure.net/"),
        new DefaultAzureCredential());
}

builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

var app = builder.Build();
app.Run();
```

**Step 3: Store client credentials in User Secrets**

```bash
dotnet user-secrets init
dotnet user-secrets set "AzureAd:TenantId" "YOUR_TENANT_ID"
dotnet user-secrets set "AzureAd:ClientId" "YOUR_CLIENT_ID"
dotnet user-secrets set "AzureAd:ClientSecret" "YOUR_CLIENT_SECRET"
```

#### Connection String Format

When using `ConnectionString` property, VapeCache supports standard Redis URI format:

```
redis://[username:password@]host:port[/database][?param=value&...]
rediss://[username:password@]host:port[/database][?param=value&...] (TLS)
```

**Examples:**

```bash
# Localhost (development)
redis://localhost:6379/0

# Production with password
redis://:YOUR_PASSWORD@redis.example.com:6380/0

# Production with TLS
rediss://:YOUR_PASSWORD@redis.example.com:6380/0

# Redis 6+ ACL with username
redis://admin:PASSWORD@redis.example.com:6379/0

# Query parameters
redis://redis.example.com:6379/0?useTls=true&connectTimeout=5000
```

**Supported Query Parameters:**
- `useTls` (bool) - Enable TLS
- `allowInvalidCert` (bool) - Skip cert validation (dev only)
- `tlsHost` (string) - SNI hostname
- `connectTimeout` (int) - Connection timeout in milliseconds
- `database` (int) - Database number (alternative to path segment)

#### Security Best Practices

1. **Never hardcode secrets in appsettings.json**
   ```json
   ❌ BAD:
   {
     "RedisConnection": {
       "Password": "super-secret-password"
     }
   }

   ✅ GOOD:
   {
     "RedisConnection": {
       "ConnectionString": null  // Loaded from Key Vault
     }
   }
   ```

2. **Use Managed Identity in production**
   - Eliminates credential management
   - Automatic rotation
   - Audit trail in Azure AD

3. **Restrict Key Vault access**
   ```bash
   # Grant minimal permissions (get + list only)
   az keyvault set-policy \
     --name myvault \
     --object-id <IDENTITY> \
     --secret-permissions get list
   ```

4. **Enable Key Vault audit logging**
   ```bash
   az monitor diagnostic-settings create \
     --resource /subscriptions/.../vaults/myvault \
     --name "audit-logs" \
     --logs '[{"category":"AuditEvent","enabled":true}]' \
     --workspace <LOG_ANALYTICS_WORKSPACE_ID>
   ```

5. **Use separate Key Vaults per environment**
   - `myvault-dev`
   - `myvault-staging`
   - `myvault-prod`

#### Troubleshooting Key Vault Integration

**Error: `Azure.RequestFailedException: Access denied`**

**Solution:** Grant Key Vault permissions to managed identity/service principal
```bash
az keyvault set-policy \
  --name myvault \
  --object-id <IDENTITY_OBJECT_ID> \
  --secret-permissions get list
```

**Error: `CredentialUnavailableException: DefaultAzureCredential failed to retrieve a token`**

**Solution:** Ensure managed identity is enabled
```bash
az webapp identity assign --name myapp --resource-group mygroup
```

**Error: `Configuration key 'RedisConnection:ConnectionString' is null`**

**Solution:** Verify Key Vault secret name matches configuration key
- Secret: `RedisConnectionString` → `IConfiguration["RedisConnection:ConnectionString"]`
- Secret: `RedisConnection--ConnectionString` → `IConfiguration["RedisConnection:ConnectionString"]`

---

## Configuration Best Practices

### 1. Separate Secrets from Configuration

**❌ Bad: Hardcode secrets in appsettings.json**
```json
{
  "RedisConnection": {
    "Password": "super-secret-password"
  }
}
```

**✅ Good: Use environment variables or KeyVault**
```bash
export RedisConnection__Password="super-secret-password"
```

Or use Azure KeyVault:
```csharp
builder.Configuration.AddAzureKeyVault(...);
```

### 2. Use appsettings.{Environment}.json for Overrides

**appsettings.json** (defaults):
```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379
  }
}
```

**appsettings.Production.json** (production overrides):
```json
{
  "RedisConnection": {
    "Host": "prod-redis.example.com",
    "Port": 6380,
    "UseTls": true
  }
}
```

### 3. Validate Configuration at Startup

Use startup preflight to fail fast if Redis is misconfigured:

```csharp
// Program.cs
var app = builder.Build();

// Validate Redis connection before serving traffic
var pool = app.Services.GetRequiredService<RedisConnectionPool>();
await pool.WarmAsync(CancellationToken.None); // Pre-create connections

app.Run();
```

### 4. Monitor Configuration Changes

Use `IOptionsMonitor<T>` to react to configuration changes at runtime:

```csharp
public class MyService
{
    private readonly IOptionsMonitor<RedisConnectionOptions> _options;

    public MyService(IOptionsMonitor<RedisConnectionOptions> options)
    {
        _options = options;
        _options.OnChange(newOptions =>
        {
            // React to configuration changes (e.g., reconnect to new host)
        });
    }
}
```

### 5. Document Configuration in README

Always include a minimal configuration example in your README:

```csharp
// Program.cs
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379
  }
}
```

---

## Complete Configuration Example

### appsettings.json (All Options)

```json
{
  "RedisConnection": {
    "Host": "localhost",
    "Port": 6379,
    "Username": null,
    "Password": null,
    "Database": 0,
    "UseTls": false,
    "AllowInvalidCert": false,
    "TlsHost": null,
    "ConnectTimeoutMs": 5000,
    "SocketConnectTimeoutMs": 5000,
    "SocketSendTimeoutMs": 5000,
    "SocketReceiveTimeoutMs": 5000
  },
  "RedisConnectionPool": {
    "MinPoolSize": 2,
    "MaxPoolSize": 10,
    "AcquireTimeoutMs": 5000,
    "IdleTimeoutMs": 300000,
    "ValidationTimeoutMs": 1000,
    "MaxInFlightPerConnection": 4096,
    "ReaperIntervalMs": 60000,
    "WarmPoolOnStartup": true
  },
  "CacheService": {
    "EnableCircuitBreaker": true,
    "CircuitBreakerFailureThreshold": 5,
    "CircuitBreakerSamplingDurationSeconds": 30,
    "CircuitBreakerBreakDurationSeconds": 60,
    "InMemoryCacheSizeLimitMb": 100,
    "InMemoryCacheExpirationScanFrequencyMinutes": 5
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "VapeCache.Infrastructure": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

### Program.cs (Complete Setup)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add VapeCache services
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

// Add OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("VapeCache.Redis");
        metrics.AddPrometheusExporter();
    })
    .WithTracing(tracing =>
    {
        tracing.AddSource("VapeCache.Redis");
        tracing.AddOtlpExporter();
    });

// Add Serilog (optional)
builder.Host.UseSerilog((context, config) =>
{
    config.ReadFrom.Configuration(context.Configuration);
});

var app = builder.Build();

// Startup preflight (validate Redis before serving traffic)
var pool = app.Services.GetRequiredService<RedisConnectionPool>();
await pool.WarmAsync(CancellationToken.None);

app.Run();
```

---

## Troubleshooting

### Connection Timeouts

**Symptom:** `RedisConnectionException: Connection attempt timed out`

**Solutions:**
1. Increase `ConnectTimeoutMs` (default: 5000ms)
2. Check network connectivity (`telnet redis-host 6379`)
3. Verify Redis is running (`redis-cli PING`)
4. Check firewall rules (allow TCP 6379)

### Pool Acquisition Timeouts

**Symptom:** `TimeoutException: Failed to acquire connection from pool`

**Solutions:**
1. Increase `MaxPoolSize` (default: 10)
2. Increase `AcquireTimeoutMs` (default: 5000ms)
3. Check `redis.pool.wait.ms` metric (high wait time = pool exhaustion)
4. Review `MaxInFlightPerConnection` (default: 4096)

### Circuit Breaker Opens Frequently

**Symptom:** Cache falls back to in-memory too often

**Solutions:**
1. Increase `CircuitBreakerFailureThreshold` (default: 5)
2. Increase `CircuitBreakerSamplingDurationSeconds` (default: 30)
3. Check `redis.cmd.failures` metric (what commands are failing?)
4. Verify Redis server health (`redis-cli INFO stats`)

### TLS Certificate Validation Fails

**Symptom:** `AuthenticationException: The remote certificate is invalid`

**Solutions:**
1. Verify certificate is CA-signed (not self-signed)
2. Check `TlsHost` matches certificate CN/SAN
3. Use `AllowInvalidCert=true` for development only
4. See [TLS_SECURITY.md](TLS_SECURITY.md) for Let's Encrypt setup

---

## See Also

- [CONFIGURATION_BEST_PRACTICES.md](CONFIGURATION_BEST_PRACTICES.md) - IOptions<T> pattern and architecture
- [QUICKSTART.md](QUICKSTART.md) - Minimal configuration example
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Metrics, traces, logs
- [TLS_SECURITY.md](TLS_SECURITY.md) - Production TLS setup
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md) - .NET Aspire configuration
