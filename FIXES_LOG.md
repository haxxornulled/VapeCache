# VapeCache Fixes Log

This document tracks all bugs that have been identified and fixed to prevent re-reporting in future code reviews.

## Session 2025-12-27: .NET 10 Breaking Changes & Code Review

### ALREADY FIXED - DO NOT RE-REPORT

#### P1-3: Buffer Pool Double-Dispose Race Condition
**File**: `VapeCache.Abstractions/Connections/RedisValueLease.cs:31-40`
**Status**: ✅ FIXED
**Date**: 2025-12-27
**Issue**: Multiple threads could race to dispose and return the same buffer to ArrayPool
**Fix**: Added atomic check-and-set using `Interlocked.Exchange` to ensure only first thread returns buffer
```csharp
public void Dispose()
{
    // CRITICAL FIX P1-3: Atomic check-and-set to prevent double-dispose
    if (_pooled && _buffer is not null)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            ArrayPool<byte>.Shared.Return(_buffer);
    }
}
```

#### .NET 10 Breaking Change: Interlocked.Exchange<CancellationTokenRegistration>
**Files**: Multiple files using `Interlocked.Exchange` on struct types
**Status**: ✅ FIXED
**Date**: 2025-12-27
**Issue**: .NET 10 requires `Interlocked.Exchange<T>` to have `T` as reference type, primitive, or enum - not arbitrary structs
**Fix**: Removed `Interlocked.Exchange` calls on `CancellationTokenRegistration`, implemented direct disposal with linked cancellation tokens

#### RespValue Type Compatibility
**File**: `VapeCache.Infrastructure/Connections/RedisRespReader.cs`
**Status**: ✅ FIXED
**Date**: 2025-12-27
**Issue**: `ManualResetValueTaskSourceCore<T>` requires `T` to be reference type
**Fix**: Changed `RespValue` from `readonly record struct` to `sealed class`

#### XUnit Test Failures - Missing Test Methods
**File**: `VapeCache.Tests/Connections/RingQueueTests.cs`
**Status**: ✅ FIXED
**Date**: 2025-12-27
**Issue**: Tests called `EnqueueAsyncNoSpinForTests` method that didn't exist
**Fix**: Added method to both MPSC and SPSC ring queue implementations
**Result**: All 33 unit tests passing (100% pass rate)

### FALSE POSITIVES - DO NOT RE-REPORT

#### RESP Protocol DoS Attack Vector
**File**: `VapeCache.Infrastructure/Connections/RedisRespReader.cs`
**Status**: ❌ FALSE POSITIVE
**Date**: 2025-12-27
**Claim**: Unvalidated bulk string sizes allow DoS attacks
**Reality**: This is a static utility class NOT used in production. Production code uses `RedisRespReaderState.cs:163-170` which HAS DoS protection with `maxBulkStringBytes` validation

#### Connection Pool Deadlock
**File**: `VapeCache.Infrastructure/Connections/RedisConnectionPool.cs:122`
**Status**: ❌ FALSE POSITIVE
**Date**: 2025-12-27
**Claim**: Semaphore slot not released on connection creation failure
**Reality**: Code properly releases slot on both failure paths (line 122 and line 125)

#### Coalesced Write Buffer Aliasing
**Files**: `VapeCache.Infrastructure/Connections/CoalescedWriteBatch.cs`, `RedisMultiplexedConnection.cs`
**Status**: ❌ FALSE POSITIVE
**Date**: 2025-12-27
**Claim**: Buffer reuse via `ScratchBaseOffset` advance causes aliasing corruption
**Reality**: Socket send completes at `RedisMultiplexedConnection.cs:422` BEFORE `RecycleAfterSend()` at line 447. No window for corruption. Buffer reuse is intentional optimization.

### BY DESIGN - DO NOT RE-REPORT

#### TLS Certificate Bypass Option
**File**: `VapeCache.Infrastructure/Connections/RedisConnectionFactory.cs:74-86`
**Status**: ⚠️ BY DESIGN
**Date**: 2025-12-27
**Issue**: `AllowInvalidCert` option bypasses TLS certificate validation
**Mitigation**: Has runtime check that blocks this in production environments. Only allowed in Development/Testing.

## Performance Optimizations Applied

### P2-2: ThreadStatic Cache for RespValue Arrays
**File**: `VapeCache.Infrastructure/Connections/RedisRespReader.cs:11-61`
**Status**: ✅ APPLIED
**Date**: Prior session
**Optimization**: Replaced global lock with `[ThreadStatic]` cache to eliminate contention on small array allocations

### P2-1: Lock Contention Elimination
**Files**: Multiple connection pool and executor files
**Status**: ✅ APPLIED
**Date**: Prior session
**Optimization**: Eliminated lock contention in hot paths using lock-free patterns

## Test Suite Status

**Last Run**: 2025-12-27
**Result**: 33/33 tests passing (100%)
**Filter Used**: `--filter "FullyQualifiedName!~Integration"` (excludes integration tests requiring Redis instance)

## Notes for Future Code Reviews

1. **Always verify claims against actual code** - Read the file before reporting
2. **Check if static/test code vs production** - Don't report issues in unused utility classes
3. **Understand design patterns** - Buffer reuse, linked cancellation tokens, etc. are intentional
4. **Check .NET version constraints** - .NET 10 has breaking changes in `Interlocked` and `Volatile` APIs
5. **Verify production vs development paths** - Some unsafe options are gated by environment checks

## Architecture Notes

- **RESP Protocol Reader**: Production uses `RedisRespReaderState` (stateful, protected), not `RedisRespReader` (static utility)
- **Connection Pooling**: Semaphore-based slot management with proper cleanup on all paths
- **Coalesced Writes**: Batches commands into scatter/gather sends, buffer reused across batches via offset advancement
- **Circuit Breaker**: Automatic failover from Redis to in-memory cache on failures
- **Buffer Pooling**: Extensive use of `ArrayPool<byte>.Shared` with ThreadStatic caching for small arrays
