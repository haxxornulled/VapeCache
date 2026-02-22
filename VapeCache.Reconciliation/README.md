# VapeCache.Reconciliation

Enterprise reconciliation for VapeCache to mitigate data loss during Redis outages.

## Highlights

- Tracks cache writes when Redis is unavailable.
- Persists operations (SQLite or in-memory backing store).
- Replays operations when Redis connectivity recovers.
- Includes hosted background reaper for automatic reconciliation.

## License Requirement

`VapeCache.Reconciliation` requires a valid VapeCache Enterprise license key.

Use environment variable:

```bash
VAPECACHE_LICENSE_KEY=VC2.<header>.<payload>.<signature>
```

## Quick Setup

```csharp
builder.Services.AddVapeCacheRedisReconciliation(licenseKey: Environment.GetEnvironmentVariable("VAPECACHE_LICENSE_KEY"));
builder.Services.AddReconciliationReaper();
```

## Documentation

- Full usage guide: `USAGE_GUIDE.md`
- Main project docs: https://github.com/haxxornulled/VapeCache
- Enterprise information: https://vapecache.com
