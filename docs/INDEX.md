# VapeCache Documentation

This index tracks the current feature set and supported APIs.

## Start Here (Juniors)
- [QUICKSTART.md](QUICKSTART.md) - Copy/paste setup from zero to first endpoint
- [CONFIGURATION.md](CONFIGURATION.md) - Every knob, sane defaults, and limits
- [API_REFERENCE.md](API_REFERENCE.md) - Exact interfaces and endpoint contracts

## Getting Started
- [README.md](../README.md) - Project overview and quick start
- [WORKFLOWS.md](WORKFLOWS.md) - CI/CD workflow maps with Mermaid diagrams
- [QUICKSTART.md](QUICKSTART.md) - Junior-friendly setup walkthrough
- [CONFIGURATION.md](CONFIGURATION.md) - Options and appsettings.json
- [NUGET_PACKAGES.md](NUGET_PACKAGES.md) - Package overview
- [.NET Aspire integration](../VapeCache.Extensions.Aspire/README.md) - Aspire usage
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md) - Detailed Aspire guide
- [WRAPPER_PLUGIN_GUIDE.md](WRAPPER_PLUGIN_GUIDE.md) - Wrapper endpoints + plugin pattern
- [BLAZOR_DASHBOARD_EXAMPLE.md](BLAZOR_DASHBOARD_EXAMPLE.md) - Realtime dashboard wiring from `/vapecache/stream`
- [ASPNETCORE_PIPELINE_CACHING.md](ASPNETCORE_PIPELINE_CACHING.md) - Output-cache pipeline hooks for MVC/Minimal API/Blazor
- [Sample app](../samples/VapeCache.Sample) - Buildable usage example

## API Reference
- [API_REFERENCE.md](API_REFERENCE.md) - Core APIs, intent model, stampede profiles, Aspire endpoints
- [TYPED_COLLECTIONS.md](TYPED_COLLECTIONS.md) - Lists, sets, hashes, sorted sets
- [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md) - Supported Redis commands
- [REDIS_MODULES.md](REDIS_MODULES.md) - Module detection + RedisJSON/RediSearch/Bloom/TimeSeries
- [FAQ.md](FAQ.md) - Common questions and behavior clarifications

## Architecture & Performance
- [ARCHITECTURE.md](ARCHITECTURE.md) - System design and data flow
- [COALESCED_WRITES.md](COALESCED_WRITES.md) - Coalesced write strategy
- [ENTERPRISE_MULTIPLEXER_AUTOSCALER.md](ENTERPRISE_MULTIPLEXER_AUTOSCALER.md) - Multiplexed lanes + autoscaler architecture and tuning
- [INMEMORY_PERSISTENCE.md](INMEMORY_PERSISTENCE.md) - In-memory fallback spill design
- [PERFORMANCE.md](PERFORMANCE.md) - Benchmark methodology and results
- [BENCHMARK_RESULTS.md](BENCHMARK_RESULTS.md) - Current benchmark snapshot (environment + latest comparison)
- [BENCHMARKING.md](BENCHMARKING.md) - How to run benchmarks
- [ENGINEERING_PLAYBOOK.md](ENGINEERING_PLAYBOOK.md) - Analyzer, profiling, and capture workflow

## Observability & Operations
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Metrics, traces, logging
- [CURRENT_BACKEND_METRIC.md](CURRENT_BACKEND_METRIC.md) - Active backend metric
- [FAILURE_SCENARIOS.md](FAILURE_SCENARIOS.md) - Redis outage behavior
- [TLS_SECURITY.md](TLS_SECURITY.md) - TLS guidance
- [LICENSE_OPERATIONS_RUNBOOK.md](LICENSE_OPERATIONS_RUNBOOK.md) - Key rotation, revocation, and incident flow
- [LICENSE_CONTROL_PLANE.md](LICENSE_CONTROL_PLANE.md) - Online revocation/kill-switch service
- [LICENSE_GENERATOR_EXTERNALIZATION.md](LICENSE_GENERATOR_EXTERNALIZATION.md) - Moving issuance/signing out of this repo

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

builder.Services.AddOptions<CacheStampedeOptions>()
    .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
    .Bind(builder.Configuration.GetSection("CacheStampede"));
```

### Basic Registration (Autofac)
```csharp
builder.RegisterModule(new VapeCache.Infrastructure.DependencyInjection.VapeCacheConnectionsModule());
builder.RegisterModule(new VapeCache.Infrastructure.DependencyInjection.VapeCacheCachingModule());
```

### Console Demo
The console demo runs background workloads and logs cache activity; see `VapeCache.Console/PLUGINS.md` for extension points and `docs/WRAPPER_PLUGIN_GUIDE.md` for optional endpoint mapping in wrapper hosts.
