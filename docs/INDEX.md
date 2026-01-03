# VapeCache Documentation

This index tracks the current feature set and supported APIs.

## Getting Started
- [README.md](../README.md) - Project overview and quick start
- [QUICKSTART.md](QUICKSTART.md) - Minimal setup walkthrough
- [CONFIGURATION.md](CONFIGURATION.md) - Options and appsettings.json
- [NUGET_PACKAGES.md](NUGET_PACKAGES.md) - Package overview
- [.NET Aspire integration](../VapeCache.Extensions.Aspire/README.md) - Aspire usage
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md) - Detailed Aspire guide
- [Sample app](../samples/VapeCache.Sample) - Buildable usage example

## API Reference
- [API_REFERENCE.md](API_REFERENCE.md) - Public API details + examples
- [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md) - Lists, sets, hashes, sorted sets
- [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md) - Supported Redis commands
- [REDIS_MODULES.md](REDIS_MODULES.md) - Module detection + RedisJSON/RediSearch/Bloom/TimeSeries
- [FAQ.md](FAQ.md) - Common questions and behavior clarifications

## Architecture & Performance
- [ARCHITECTURE.md](ARCHITECTURE.md) - System design and data flow
- [COALESCED_WRITES.md](COALESCED_WRITES.md) - Coalesced write strategy
- [INMEMORY_PERSISTENCE.md](INMEMORY_PERSISTENCE.md) - In-memory fallback spill design
- [PERFORMANCE.md](PERFORMANCE.md) - Benchmark methodology and results
- [BENCHMARKING.md](BENCHMARKING.md) - How to run benchmarks

## Observability & Operations
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Metrics, traces, logging
- [CURRENT_BACKEND_METRIC.md](CURRENT_BACKEND_METRIC.md) - Active backend metric
- [FAILURE_SCENARIOS.md](FAILURE_SCENARIOS.md) - Redis outage behavior
- [TLS_SECURITY.md](TLS_SECURITY.md) - TLS guidance

## Roadmap & Risk
- [FUTURE_PROOFING.md](FUTURE_PROOFING.md) - Hardening notes and risk assessment
- [API_EXPANSION_PLAN.md](API_EXPANSION_PLAN.md) - Planned API expansions
- [PHASE_2_3_COMPLETE.md](PHASE_2_3_COMPLETE.md) - Phase status and roadmap
- [GAP_ANALYSIS.md](GAP_ANALYSIS.md) - Known gaps and coverage
- [NON_GOALS.md](NON_GOALS.md) - Explicit non-goals

## Contributing
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines
- [BENCHMARKING_STATUS.md](BENCHMARKING_STATUS.md) - Benchmark coverage status

## Quick Reference

### Install
```bash
dotnet add package VapeCache
dotnet add package VapeCache.Extensions.Aspire
```

### Basic Registration (Microsoft DI)
```csharp
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();
```

### Basic Registration (Autofac)
```csharp
builder.RegisterModule(new VapeCache.Infrastructure.DependencyInjection.VapeCacheConnectionsModule());
builder.RegisterModule(new VapeCache.Infrastructure.DependencyInjection.VapeCacheCachingModule());
```

### Console Demo
The console demo runs background workloads and logs cache activity. It does not expose HTTP endpoints.
