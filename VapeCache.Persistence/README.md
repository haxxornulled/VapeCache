# VapeCache.Persistence - Enterprise In-Memory Spill-to-Disk

**ENTERPRISE ONLY** - Requires VapeCache Enterprise License ($499/month)

## Overview

VapeCache.Persistence provides enterprise-grade spill-to-disk functionality for in-memory caching, preventing out-of-memory errors by transparently persisting large cache values to disk with encryption at rest.

## Key Features

### Scatter/Gather Distribution
- **65,536-directory hash distribution** prevents filesystem bottlenecks
- Avoids inode exhaustion and directory lookup slowdowns
- Production-tested at scale

### Encryption at Rest
- Pluggable `ISpillEncryptionProvider` interface
- Supports AES-256, custom HSM integration
- Compliant with GDPR/HIPAA encryption requirements

### Performance Optimizations
- **Inline prefix caching** - First N bytes stay in-memory for fast access
- **Atomic writes** - Temp file + atomic move prevents corruption
- **Automatic orphan cleanup** - Background GC with configurable interval
- **Full telemetry** - Metrics for spill writes, reads, cleanup operations

## Installation

```bash
dotnet add package VapeCache.Persistence
```

## License Requirement

This package requires a valid VapeCache Enterprise license key. Set the environment variable:

```bash
export VAPECACHE_LICENSE_KEY="VCENT-YOUR-CUSTOMER-ID-..."
```

Or configure in `appsettings.json`:

```json
{
  "VapeCache": {
    "LicenseKey": "VCENT-YOUR-CUSTOMER-ID-..."
  }
}
```

## Usage

```csharp
using VapeCache.Persistence;

services.AddVapeCachePersistence(options =>
{
    options.EnableSpillToDisk = true;
    options.SpillThresholdBytes = 1024 * 1024; // 1 MB
    options.InlinePrefixBytes = 256;           // First 256 bytes in-memory
    options.SpillDirectory = "/var/cache/vapecache/spill";
    options.EnableOrphanCleanup = true;
    options.OrphanMaxAge = TimeSpan.FromDays(7);
});
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `EnableSpillToDisk` | `false` | Enable spill-to-disk feature |
| `SpillThresholdBytes` | `1048576` (1MB) | Size threshold for spilling to disk |
| `InlinePrefixBytes` | `256` | Bytes to keep in-memory for fast access |
| `SpillDirectory` | `%LOCALAPPDATA%/VapeCache/spill` | Disk storage location |
| `EnableOrphanCleanup` | `true` | Auto-cleanup orphaned spill files |
| `OrphanMaxAge` | `7 days` | Max age before cleanup |
| `OrphanCleanupInterval` | `1 hour` | Cleanup frequency |

## Architecture

```
InMemoryCacheService
  ├── In-Memory (< threshold)
  │   └── Direct MemoryCache storage
  │
  └── Spill-to-Disk (>= threshold)
      ├── Inline Prefix (first N bytes in-memory)
      ├── Encrypted Tail (on disk)
      └── Scatter/Gather (66K directories)
          └── /spill/ab/cd/abcdef123....bin
```

## Pricing

- **Enterprise**: $499/month, unlimited instances
- Includes: Reconciliation + Persistence + Multi-region + Compliance + 24/7 support

Contact: sales@vapecache.com
Docs: https://vapecache.com/docs/persistence
