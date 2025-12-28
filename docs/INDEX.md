# VapeCache Documentation Index

**Last Updated:** December 25, 2025

## Quick Links

### Getting Started
- [README.md](../README.md) - Main project overview and quick start
- [QUICKSTART.md](QUICKSTART.md) - Get running in 5 minutes
- [CONFIGURATION.md](CONFIGURATION.md) - appsettings.json reference

### .NET Aspire Integration ⭐ NEW
- [VapeCache.Extensions.Aspire/README.md](../VapeCache.Extensions.Aspire/README.md) - Package usage guide
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md) - Complete integration guide
- [ASPIRE_PACKAGE_SUMMARY.md](ASPIRE_PACKAGE_SUMMARY.md) - Implementation details
- [ASPIRE_CACHE_METRICS.md](ASPIRE_CACHE_METRICS.md) - Metrics specification

### Development
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines
- [BENCHMARKING.md](BENCHMARKING.md) - Performance benchmarking guide

## Architecture & Design

### Core Architecture
- [ARCHITECTURE.md](ARCHITECTURE.md) - High-level design overview
- [COALESCED_WRITES.md](COALESCED_WRITES.md) - Why we're 5-30% faster than StackExchange.Redis
- [PERFORMANCE.md](PERFORMANCE.md) - Performance deep-dive and benchmark methodology

### Configuration
- [CONFIGURATION_BEST_PRACTICES.md](CONFIGURATION_BEST_PRACTICES.md) - IOptions<T> pattern and KeyVault integration
- [TLS_SECURITY.md](TLS_SECURITY.md) - Production TLS best practices

## Observability

### Metrics, Traces & Logs
- [OBSERVABILITY_ARCHITECTURE.md](OBSERVABILITY_ARCHITECTURE.md) - Comprehensive observability guide
  - OpenTelemetry metrics (20+ metrics)
  - Distributed tracing (Activity spans)
  - Structured logging (ILogger<T>)
  - SEQ integration
  - Prometheus + Grafana setup
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md#aspire-dashboard-integration) - Aspire Dashboard metrics
- [CURRENT_BACKEND_METRIC.md](CURRENT_BACKEND_METRIC.md) - Real-time backend visibility (new!)

### Monitoring
- [BENCHMARKING.md](BENCHMARKING.md#production-monitoring) - Key metrics to track in production
- [CURRENT_BACKEND_METRIC.md](CURRENT_BACKEND_METRIC.md) - Track which backend is active (Redis vs in-memory)

## API Reference

### Command Support
- [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md) - What Redis commands are supported
- [API_EXPANSION_PLAN.md](API_EXPANSION_PLAN.md) - Roadmap to 200+ commands
- [NON_GOALS.md](NON_GOALS.md) - What VapeCache is **not**

## Operations

### Production Guidance
- [FAILURE_SCENARIOS.md](FAILURE_SCENARIOS.md) - What happens when Redis fails
- [TLS_SECURITY.md](TLS_SECURITY.md) - Securing Redis connections in production

### Performance
- [BENCHMARKING.md](BENCHMARKING.md) - How to benchmark VapeCache
- [PERFORMANCE.md](PERFORMANCE.md) - Why VapeCache beats StackExchange.Redis

## Package Documentation

### VapeCache.Extensions.Aspire
- [Package README](../VapeCache.Extensions.Aspire/README.md) - Quick start and API reference
- [Integration Guide](ASPIRE_INTEGRATION.md) - Comprehensive Aspire integration
- [Package Summary](ASPIRE_PACKAGE_SUMMARY.md) - Implementation details and file structure
- [Metrics Reference](ASPIRE_CACHE_METRICS.md) - All available cache metrics

## Contributing

### How to Contribute
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines
  - Code style standards
  - Testing requirements
  - Performance standards
  - Git workflow
  - PR process

### Development Resources
- [BENCHMARKING.md](BENCHMARKING.md#custom-benchmarks) - How to write benchmarks
- [API_EXPANSION_PLAN.md](API_EXPANSION_PLAN.md) - Commands we need implemented

## Document Categories

### ✅ Complete
- Main README
- Aspire Integration (full package + docs)
- Benchmarking Guide
- Contributing Guidelines
- Architecture Docs
- Configuration Docs
- Observability Docs
- API Reference

### 📋 Planned
- Example applications (Blazor + Aspire)
- Advanced recipes (Lua scripting, Pub/Sub)
- Migration guides (from StackExchange.Redis)
- Troubleshooting guide

## Document Status

| Document | Status | Last Updated |
|----------|--------|--------------|
| README.md | ✅ Complete | 2025-12-25 |
| CONTRIBUTING.md | ✅ Complete | 2025-12-25 |
| BENCHMARKING.md | ✅ Complete | 2025-12-25 |
| ASPIRE_INTEGRATION.md | ✅ Complete | 2025-12-25 |
| ASPIRE_PACKAGE_SUMMARY.md | ✅ Complete | 2025-12-25 |
| ASPIRE_CACHE_METRICS.md | ✅ Complete | Earlier |
| VapeCache.Extensions.Aspire/README.md | ✅ Complete | 2025-12-25 |
| ARCHITECTURE.md | ✅ Complete | Earlier |
| COALESCED_WRITES.md | ✅ Complete | Earlier |
| CONFIGURATION.md | ✅ Complete | Earlier |
| CONFIGURATION_BEST_PRACTICES.md | ✅ Complete | Earlier |
| OBSERVABILITY_ARCHITECTURE.md | ✅ Complete | Earlier |
| PERFORMANCE.md | ✅ Complete | Earlier |
| REDIS_PROTOCOL_SUPPORT.md | ✅ Complete | Earlier |
| API_EXPANSION_PLAN.md | ✅ Complete | Earlier |
| NON_GOALS.md | ✅ Complete | Earlier |
| FAILURE_SCENARIOS.md | ✅ Complete | Earlier |
| TLS_SECURITY.md | ✅ Complete | Earlier |
| QUICKSTART.md | ✅ Complete | Earlier |

## Quick Reference

### Installation
```bash
# Core library
dotnet add package VapeCache.Infrastructure

# .NET Aspire integration
dotnet add package VapeCache.Extensions.Aspire
```

### Basic Usage
```csharp
// Standard .NET
builder.Services.AddVapecacheRedisConnections();
builder.Services.AddVapecacheCaching();

// With .NET Aspire
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry();
```

### Key Metrics (Aspire Dashboard)
- `cache.get.hits` - Cache hits
- `cache.get.misses` - Cache misses
- `cache.fallback.to_memory` - Circuit breaker activations
- `redis.cmd.ms` - Redis command latency
- `redis.pool.wait.ms` - Connection pool wait time

### Health Checks
- `/health` - Overall health
- `/health/ready` - Kubernetes readiness
- `/health/live` - Kubernetes liveness

## Support

- **GitHub Issues**: [Report bugs or request features](https://github.com/haxxornulled/VapeCache/issues)
- **Discussions**: [Ask questions or share ideas](https://github.com/haxxornulled/VapeCache/discussions)
- **Documentation**: This index and all linked docs

---

**VapeCache** - Enterprise-grade Redis caching for .NET 10
