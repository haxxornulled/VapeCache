# VapeCache.ServiceDefaults

`VapeCache.ServiceDefaults` contains shared Aspire and OpenTelemetry service defaults used by VapeCache hosts.

## Use This Package When

- you want OpenTelemetry tracing, metrics, and log export defaults
- you want health checks and resilience defaults for hosted services
- you want common service-discovery and HTTP resilience wiring

## Example

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.AddServiceDefaults();
```

This shared project is referenced from app-host and API projects to keep distributed-service defaults consistent across environments.

