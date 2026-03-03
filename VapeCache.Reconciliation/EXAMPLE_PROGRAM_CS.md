# Example Program.cs - VapeCache.Reconciliation Setup

## Minimal Setup (Recommended)

```csharp
using VapeCache.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

// Set license key via environment variable: VAPECACHE_LICENSE_KEY=VC2....

// Add VapeCache reconciliation (reads license from environment)
builder.Services.AddVapeCacheRedisReconciliation();

// Add Reaper background service for automatic reconciliation every 30 seconds
builder.Services.AddReconciliationReaper();

var app = builder.Build();
app.Run();
```

---

## Full Configuration Example

```csharp
using VapeCache.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

// Get license key from appsettings.json or environment variable
var licenseKey = builder.Configuration["VapeCache:LicenseKey"]
    ?? Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY")
    ?? throw new InvalidOperationException("VapeCache license key not found");

// Add Redis reconciliation with explicit configuration
builder.Services.AddVapeCacheRedisReconciliation(
    licenseKey: licenseKey,
    configure: options =>
    {
        // Core reconciliation settings
        options.Enabled = true;
        options.MaxPendingOperations = 100_000;  // Advisory pending threshold (tracking continues in no-drop mode)
        options.MaxOperationsPerRun = 1_000;     // Process 1K ops per run
        options.BatchSize = 100;                 // Batch size for SQL/Redis ops

        // Operation lifecycle
        options.MaxOperationAge = TimeSpan.FromHours(1);      // Skip ops older than 1 hour
        options.MaxRunDuration = TimeSpan.FromMinutes(5);     // Max 5 min per reconciliation run

        // Failure handling
        options.InitialBackoff = TimeSpan.FromMilliseconds(100);
        options.MaxBackoff = TimeSpan.FromSeconds(5);
        options.BackoffMultiplier = 2.0;
        options.MaxConsecutiveFailures = 10;  // Stop after 10 consecutive Redis failures
    },
    configureStore: store =>
    {
        // Use SQLite for production (persists across restarts)
        store.UseSqlite = true;
        store.DatabasePath = Path.Combine(
            builder.Environment.ContentRootPath,
            "Data",
            "reconciliation.db");
        store.BusyTimeoutMs = 5000;  // 5 second timeout for SQLite lock contention
    });

// Add the Reaper background service
builder.Services.AddReconciliationReaper(reaper =>
{
    reaper.Enabled = true;
    reaper.Interval = TimeSpan.FromSeconds(30);       // Run every 30 seconds
    reaper.InitialDelay = TimeSpan.FromSeconds(10);   // Wait 10s after startup
});

// Optional: Add admin endpoints for manual control
builder.Services.AddControllers();

var app = builder.Build();

// Optional: Manual reconciliation endpoint
app.MapPost("/admin/reconcile", async (IRedisReconciliationService reconciliation) =>
{
    var pendingBefore = reconciliation.PendingOperations;
    await reconciliation.ReconcileAsync();
    var pendingAfter = reconciliation.PendingOperations;

    return Results.Ok(new
    {
        PendingBefore = pendingBefore,
        Synced = pendingBefore - pendingAfter,
        Remaining = pendingAfter
    });
});

// Optional: Check pending operations
app.MapGet("/admin/reconciliation/status", (IRedisReconciliationService reconciliation) =>
{
    return Results.Ok(new
    {
        PendingOperations = reconciliation.PendingOperations
    });
});

// Optional: Flush all pending operations (DANGEROUS - data loss!)
app.MapPost("/admin/reconciliation/flush", async (IRedisReconciliationService reconciliation) =>
{
    await reconciliation.FlushAsync();
    return Results.Ok("All pending operations cleared");
});

app.MapControllers();
app.Run();
```

---

## With appsettings.json

**appsettings.json:**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "VapeCache.Reconciliation": "Debug"
    }
  },
  "VapeCache": {
    "LicenseKey": "VC2.<base64url-header>.<base64url-payload>.<base64url-signature>"
  },
  "RedisReconciliation": {
    "Enabled": true,
    "MaxPendingOperations": 100000,
    "MaxOperationsPerRun": 1000,
    "BatchSize": 100,
    "MaxOperationAge": "01:00:00",
    "MaxRunDuration": "00:05:00",
    "InitialBackoff": "00:00:00.100",
    "MaxBackoff": "00:00:05",
    "BackoffMultiplier": 2.0,
    "MaxConsecutiveFailures": 10
  },
  "RedisReconciliationStore": {
    "UseSqlite": true,
    "DatabasePath": "Data/reconciliation.db",
    "BusyTimeoutMs": 5000
  },
  "RedisReconciliationReaper": {
    "Enabled": true,
    "Interval": "00:00:30",
    "InitialDelay": "00:00:10"
  }
}
```

**Program.cs:**
```csharp
using VapeCache.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

// Read all configuration from appsettings.json
builder.Services.AddVapeCacheRedisReconciliation(
    builder.Configuration,
    licenseKey: builder.Configuration["VapeCache:LicenseKey"]);

// Add Reaper with configuration from appsettings.json
builder.Services.AddReconciliationReaper(builder.Configuration);

var app = builder.Build();
app.Run();
```

---

## Environment-Specific Configuration

**appsettings.Development.json:**
```json
{
  "RedisReconciliationReaper": {
    "Enabled": false  // Disable Reaper in development
  },
  "RedisReconciliationStore": {
    "UseSqlite": false  // Use in-memory store for testing
  }
}
```

**appsettings.Production.json:**
```json
{
  "RedisReconciliation": {
    "MaxPendingOperations": 500000,  // Higher limit for production
    "MaxOperationsPerRun": 5000
  },
  "RedisReconciliationReaper": {
    "Interval": "00:00:10"  // Run every 10 seconds in production
  },
  "RedisReconciliationStore": {
    "DatabasePath": "/var/lib/vapecache/reconciliation.db"  // Production path
  }
}
```

---

## Docker / Kubernetes Setup

**Dockerfile:**
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

# Create directory for SQLite database
RUN mkdir -p /app/Data

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MyApp/MyApp.csproj", "MyApp/"]
RUN dotnet restore "MyApp/MyApp.csproj"
COPY . .
WORKDIR "/src/MyApp"
RUN dotnet build "MyApp.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MyApp.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

**Kubernetes Deployment:**
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: myapp
spec:
  replicas: 3
  template:
    spec:
      containers:
      - name: myapp
        image: myapp:latest
        env:
        - name: VAPECACHE_LICENSE_KEY
          valueFrom:
            secretKeyRef:
              name: vapecache-license
              key: license-key
        volumeMounts:
        - name: reconciliation-data
          mountPath: /app/Data
      volumes:
      - name: reconciliation-data
        persistentVolumeClaim:
          claimName: reconciliation-pvc
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: reconciliation-pvc
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 10Gi
---
apiVersion: v1
kind: Secret
metadata:
  name: vapecache-license
type: Opaque
data:
  license-key: VkNFTlQtYWNtZS0xMjM0NTY3ODkwLUFCQzEyMy4uLg==  # Base64 encoded
```

**Program.cs for Docker/K8s:**
```csharp
using VapeCache.Reconciliation;

var builder = WebApplication.CreateBuilder(args);

// License from environment variable (set in K8s deployment)
var licenseKey = Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY")
    ?? throw new InvalidOperationException("VAPECACHE_LICENSE_KEY environment variable not set");

builder.Services.AddVapeCacheRedisReconciliation(
    licenseKey: licenseKey,
    configureStore: store =>
    {
        // Use mounted volume for SQLite database
        store.DatabasePath = "/app/Data/reconciliation.db";
    });

builder.Services.AddReconciliationReaper();

var app = builder.Build();
app.Run();
```

---

## Testing Setup (No Reaper)

For unit/integration tests where you want manual control:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using VapeCache.Reconciliation;

public class ReconciliationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReconciliationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Add reconciliation WITHOUT the Reaper
                services.AddVapeCacheRedisReconciliation(
                    licenseKey: "VC2.<base64url-header>.<base64url-payload>.<base64url-signature>");

                // Use in-memory store for tests (no SQLite)
                services.UseInMemoryBackingStore();
            });
        });
    }

    [Fact]
    public async Task Reconciliation_SyncsTrackedOperations()
    {
        // Arrange
        var scope = _factory.Services.CreateScope();
        var reconciliation = scope.ServiceProvider
            .GetRequiredService<IRedisReconciliationService>();

        // Act: Track some writes
        reconciliation.TrackWrite("key1", "value1"u8.ToArray(), TimeSpan.FromMinutes(5));
        reconciliation.TrackWrite("key2", "value2"u8.ToArray(), TimeSpan.FromMinutes(5));

        // Assert: Pending operations
        Assert.Equal(2, reconciliation.PendingOperations);

        // Act: Manually trigger reconciliation
        await reconciliation.ReconcileAsync();

        // Assert: Operations synced
        Assert.Equal(0, reconciliation.PendingOperations);
    }
}
```

---

## License Key Management

### Option 1: appsettings.json (Development only)
```json
{
  "VapeCache": {
    "LicenseKey": "VC2.<base64url-header>.<base64url-payload>.<base64url-signature>"
  }
}
```

### Option 2: Environment Variable (Recommended for Production)
```bash
export VAPECACHE_LICENSE_KEY="VC2.<base64url-header>.<base64url-payload>.<base64url-signature>"
```

### Option 3: Azure Key Vault
```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri("https://myvault.vault.azure.net/"),
    new DefaultAzureCredential());

// Key Vault secret named "VapeCache--LicenseKey"
var licenseKey = builder.Configuration["VapeCache:LicenseKey"];
builder.Services.AddVapeCacheRedisReconciliation(licenseKey);
```

### Option 4: AWS Secrets Manager
```csharp
builder.Configuration.AddSecretsManager(
    configurator: options =>
    {
        options.SecretFilter = entry => entry.Name.StartsWith("VapeCache");
    });

var licenseKey = builder.Configuration["VapeCache:LicenseKey"];
builder.Services.AddVapeCacheRedisReconciliation(licenseKey);
```

---

## Next Steps

1. **Set license key**: Get your Enterprise license at https://vapecache.com/enterprise
2. **Configure reconciliation**: Start with the minimal setup and tune based on your traffic
3. **Monitor metrics**: Set up OpenTelemetry/Prometheus dashboards
4. **Set up alerts**: Alert on high pending operations and any non-zero drop rate
5. **Test failover**: Simulate Redis outages and verify reconciliation works

**Support**: For Enterprise customers, reach out to support@vapecache.com for setup assistance.
