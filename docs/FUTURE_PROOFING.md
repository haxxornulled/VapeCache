# Future Proofing Notes

This document captures the recent hardening work, the current risk profile, and the forward‑looking plan so consumers can trust the engineering and developer experience behind VapeCache.

## Scope

These notes cover:
- Correctness + thread safety fixes in fallback execution
- Test coverage improvements
- Documentation alignment
- Security and performance risks with mitigation strategy

## Recent Hardening Work

### Correctness + Thread Safety
- In‑memory fallback set operations now lock per entry to avoid concurrent corruption and double‑remove races.
- Hash set semantics now return correct “new field” indicators under contention.
- `LRangeAsync` now returns an empty array when lists are emptied concurrently.

### Test Coverage
- Added a concurrent set‑add stress test for the in‑memory executor.
- Added a hash “new field” behavior test.
- Added RESP reader limit tests for bulk size and array depth.
- Added a spill-to-disk round-trip test for the in-memory fallback.

### Warning Cleanup
- Fixed nullability warnings in `ResultExtensions` by forcing nullable match result types.

### Documentation + DX Alignment
- Rebuilt docs index and aligned API/config docs with current feature set.
- Updated README examples to compile and match real options.
- Replaced speculative API expansion claims with a scoped backlog.
- Added a buildable sample (`samples/VapeCache.Sample`) to validate examples.

### Spill Support
- Implemented async spill-to-disk for large fallback payloads with pluggable encryption.
- Added spill metrics and orphan cleanup tracking.

## Developer Experience Commitments

We design the library so consumer code is predictable, boring, and buildable:
- **Explicit behavior**: documented fallbacks and command coverage.
- **Buildable examples**: sample app compiles against the current API.
- **Autofac‑first**: registrations follow the real DI pattern used in the repo.
- **Clear non‑goals**: no Pub/Sub, Lua scripting, RESP3, or cluster support.

## Security Risk Assessment

### TLS Misconfiguration (Medium)
**Risk:** dev/test settings like `AllowInvalidCert` are easy to misuse.  
**Current:** production blocks invalid certs.  
**Mitigation:** keep docs explicit; add runtime warnings if enabled outside development.

### Credential Handling (Medium)
**Risk:** connection strings may be logged or leaked by host configuration.  
**Current:** library avoids logging secrets; host decides logging.  
**Mitigation:** document redaction; add safe logging helpers for connection options.

### RESP Response Limits (Medium)
**Risk:** large bulk responses can cause memory pressure or DoS.  
**Current:** limits are enforced in the RESP readers with tests.  
**Mitigation:** keep limits configurable; expand coverage for additional edge frames.

### Fallback Divergence (Low)
**Risk:** in‑memory fallback semantics may differ from Redis edge behaviors.  
**Mitigation:** document parity gaps and add targeted tests for TTL and type conflicts.

## Performance Risk Assessment

### In‑Memory Fallback Allocations (Medium)
**Risk:** prolonged outages can allocate heavily via `ToArray()`.  
**Mitigation:** consider pooling for hot paths or cap fallback memory with `MemoryCacheOptions`.

### Large Batch Buffers (Low/Medium)
**Risk:** `MSET` builds large command buffers; big batches spike memory.  
**Mitigation:** document recommended batch sizes; add guardrails for extreme payloads.

### Coalesced Send Copy Cost (Low)
**Risk:** coalesced write packing copies into a contiguous buffer.  
**Mitigation:** keep as‑is for correctness; validate with benchmarks.

### Backpressure Visibility (Medium)
**Risk:** queue saturation is not obvious to consumers.  
**Mitigation:** queue depth and wait time metrics are emitted; document fast‑fail `Try*` usage.

## Forward‑Looking Hardening Plan

1. **Document fallback parity gaps** and add integration tests for edge cases.
2. **Add spill retention guidance** for operators (max age, disk sizing).
3. **Add optional spill integrity checks** for corrupted files.

## References

- [CONFIGURATION.md](CONFIGURATION.md)
- [API_REFERENCE.md](API_REFERENCE.md)
- [REDIS_PROTOCOL_SUPPORT.md](REDIS_PROTOCOL_SUPPORT.md)
- [INMEMORY_PERSISTENCE.md](INMEMORY_PERSISTENCE.md)
