# VapeCache.ServiceDefaults

`VapeCache.ServiceDefaults` contains shared Aspire and OpenTelemetry service defaults used by VapeCache hosts.

## What It Configures

- OpenTelemetry tracing, metrics, and log export defaults
- Health checks and resilience defaults for hosted services
- Common service-discovery and HTTP resilience wiring

## Intended Usage

Reference this package from app-host projects to keep distributed-service defaults consistent across environments.

