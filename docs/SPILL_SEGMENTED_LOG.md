# Segmented Spill Log Design

This document describes the high-throughput spill engine used by `SegmentedLogSpillStore`.

## Design Summary
- Append-only segmented spill log.
- Preallocated segment files.
- Offset-based positional I/O via `RandomAccess.ReadAsync` / `RandomAccess.WriteAsync`.
- In-memory index: `spillRef -> {segmentId, offset, length, flags, crc}`.
- Background maintenance:
  - Compact closed segments with high dead-byte ratio.
  - Delete fully dead retired segments after a grace window.
  - Optional orphan cleanup for stale segment files.

## Why This Pattern
- Appends keep disk writes sequential and avoid random in-place update penalties.
- Positional I/O supports safe concurrent access without a shared stream cursor.
- Preallocation reduces file growth churn and metadata overhead.
- Compaction reclaims dead bytes without blocking hot reads/writes.

## Record Layout
Each record uses a fixed-size header plus payload:

- `magic` (4 bytes)
- `version` (1 byte)
- `flags` (1 byte)
- `reserved` (2 bytes)
- `spillRef` (16 bytes)
- `payloadLength` (4 bytes)
- `crc32` (4 bytes)
- `payload` (`payloadLength` bytes)

## Segment Lifecycle
1. Active segment receives appends until capacity is reached.
2. Writer rotates to a new preallocated segment.
3. Closed segments are compacted when dead bytes are high.
4. Retired segments are deleted after grace-period + zero live references.

## Registration
Default OSS wiring remains `NoopSpillStore`. To enable file-backed segmented spill:

```csharp
builder.Services.AddVapeCacheCaching();
builder.Services.AddVapeCachePersistence();
```

For Autofac hosts:

```csharp
builder.RegisterModule(new VapeCacheCachingModule());
builder.RegisterVapeCachePersistence();
```

## Notes
- `InMemoryCacheService` still honors enterprise feature gating (`IsDurableSpillLicensed`).
- Encryption is supported by registering `ISpillEncryptionProvider`.
- Spill is best-effort fallback, not a transactional durability layer.
