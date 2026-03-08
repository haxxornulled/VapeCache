# In-Memory Persistence and Scatter Spill Design

This document describes the spill layer for `InMemoryCache` using a scatter spill pattern: large values live on disk while a compact in-memory entry holds the key metadata and a pointer (Guid) to the on-disk blob.

## Goals
- Keep the in-memory fallback fast for small values.
- Cap memory growth during long Redis outages.
- Avoid blocking application threads on disk I/O.
- Preserve TTL behavior and keep failure semantics "fail-open".
- Make spill behavior observable and configurable.

## Non-Goals
- Strong durability guarantees (this is a best-effort fallback).
- Transactional consistency across multiple keys.
- Full Redis feature parity on disk-backed data.

## Entry Model
Each cache entry stores metadata in memory and may include a disk reference:

```
CacheEntry
  - InlinePrefix: byte[]?  (optional small prefix)
  - SpillRef: Guid?        (identifier for disk data)
  - Length: int
  - ExpiresAt: DateTimeOffset?
```

The in-memory dictionary maps the key to `CacheEntry`. When `ValueState == Spill`, the value is stored on disk using a `SpillRef` plus metadata.

## Scatter Spill Layout
Large values are written to a single file per spill reference:

```
spill/{aa}/{bb}/{spillRef}.bin
```

The `{aa}` and `{bb}` shards are derived from the Guid to keep directories balanced. Each file stores the encrypted tail bytes for that entry.

## Read Path
1. Lookup key in memory.
2. If expired, remove and return miss.
3. If `InMemory`, return value.
4. If `Spill`, read segments from disk and assemble value.
5. If a read fails, treat as miss and remove entry (fail-open).

## Write Path
1. If payload size <= `SpillThresholdBytes`, store in memory.
2. Otherwise:
   - Create `SpillRef` Guid.
   - Write tail bytes to disk using async I/O.
   - Store `CacheEntry` in memory with `SpillRef` + inline prefix.

Writes are awaited (async) so reads never observe a missing spill file.

Inline prefix:
- Optional: keep the first N bytes in memory (`InlinePrefixBytes`) to serve partial reads or small range calls without disk I/O.

## Delete and Expiry
- Deletes remove the entry from memory and schedule best-effort spill cleanup.
- Expired entries are removed on access and cleaned up via eviction callbacks.
- Disk cleanup is best-effort and does not block requests.

## Orphan Cleanup
Spill files can be orphaned after process crashes or restarts. When enabled, the spill store
periodically scans the spill directory and deletes files older than `OrphanMaxAge`. Cleanup
is best-effort and runs asynchronously on the next cache operation after the interval elapses.

## Concurrency Model
- File-per-spill avoids shared writer contention.
- MemoryCache handles key-level concurrency.
- Spill deletes are fire-and-forget to keep hot paths fast.

## Failure Handling
- Disk write failure: fallback to storing the value in memory.
- Disk read failure: treat as miss and remove the spill entry.
- Process crash: in-memory entries are lost; spilled files are orphaned until cleanup.

## Configuration Surface
Proposed options:
- `EnableSpillToDisk` (bool)
- `SpillThresholdBytes` (int)
- `InlinePrefixBytes` (int)
- `SpillDirectory` (string)
- `EnableOrphanCleanup` (bool)
- `OrphanCleanupInterval` (TimeSpan)
- `OrphanMaxAge` (TimeSpan)

## Metrics
Add counters and gauges:
- `cache.spill.write.count`, `cache.spill.write.bytes`
- `cache.spill.read.count`, `cache.spill.read.bytes`
- `cache.spill.orphan.scanned`
- `cache.spill.orphan.cleanup.count`, `cache.spill.orphan.cleanup.bytes`

## Decisions
- Spill reads and writes are fully async.
- Encryption at rest is supported via `ISpillEncryptionProvider`.
- Default thresholds: `SpillThresholdBytes = 262144`, `InlinePrefixBytes = 4096`, `EnableSpillToDisk = false`.
- OSS/default wiring uses a no-op spill store; register `VapeCache.Persistence` (`AddVapeCachePersistence(...)`) to enable file-backed scatter spill.
- Orphan cleanup is opt-in (`EnableOrphanCleanup = false`, `OrphanMaxAge = 7 days`).

## Tests to Add
- Spill threshold boundary (exactly threshold and threshold + 1).
- Expiry cleanup removes spill refs.
- Disk read failure yields miss and cleans entry.

## Open Questions
- Should spill data be shared across keys when values are identical?

## Sample Encryption Provider
Below is a minimal, production-appropriate provider using AES-GCM. It follows the
`ISpillEncryptionProvider` contract and uses an injected key from options.

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

public sealed class SpillEncryptionOptions
{
    public string Base64Key { get; set; } = string.Empty; // 32 bytes, base64
}

public sealed class AesGcmSpillEncryptionProvider : ISpillEncryptionProvider
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmSpillEncryptionProvider(IOptions<SpillEncryptionOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.Base64Key);
        if (_key.Length != 32)
            throw new InvalidOperationException("Spill encryption key must be 32 bytes.");
    }

    public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key);
        aes.Encrypt(nonce, plaintext.Span, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize + TagSize, ciphertext.Length);
        return ValueTask.FromResult(output);
    }

    public ValueTask<byte[]> DecryptAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken ct)
    {
        if (ciphertext.Length < NonceSize + TagSize)
            throw new InvalidOperationException("Invalid spill ciphertext.");

        var nonce = ciphertext.Slice(0, NonceSize);
        var tag = ciphertext.Slice(NonceSize, TagSize);
        var payload = ciphertext.Slice(NonceSize + TagSize);

        var plaintext = new byte[payload.Length];
        using var aes = new AesGcm(_key);
        aes.Decrypt(nonce.Span, payload.Span, tag.Span, plaintext);
        return ValueTask.FromResult(plaintext);
    }
}
```

Registration example:

```csharp
builder.Services.Configure<InMemorySpillOptions>(o =>
{
    o.EnableSpillToDisk = true;
    o.SpillThresholdBytes = 256 * 1024;
    o.InlinePrefixBytes = 4096;
    o.EnableOrphanCleanup = true;
    o.OrphanMaxAge = TimeSpan.FromDays(7);
});

builder.Services.Configure<SpillEncryptionOptions>(o =>
{
    o.Base64Key = "<your 32-byte base64 key>";
});

builder.Services.AddSingleton<ISpillEncryptionProvider, AesGcmSpillEncryptionProvider>();
```
