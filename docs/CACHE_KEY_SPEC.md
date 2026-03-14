# Cache Key Specification

This document defines key formats, normalization rules, and reserved key namespaces used by VapeCache runtime components.

## 1. Scope

This spec covers:

- typed and untyped cache key contracts
- region key composition
- tag/zone version key namespaces
- ASP.NET output-cache store key namespaces
- tagged entry envelope formats

## 2. Key Contracts

### 2.1 Public key types

- `CacheKey` wraps an untyped key string.
- `CacheKey<T>` wraps a typed key string and can be implicitly converted to `CacheKey`.

Key values are treated as opaque, case-sensitive strings.

### 2.2 Region keys

`IVapeCache.Region(name).Key<T>(id)` produces:

`{region}:{id}`

Rules:

- `region` must be non-empty/non-whitespace
- `id` must be non-empty/non-whitespace
- no additional normalization or escaping is applied

## 3. Key Normalization Rules

### 3.1 General cache keys

General cache keys are not trimmed, lowercased, or canonicalized by default.

Stampede validation (`StampedeProtectedCacheService`) applies only on `GetOrSetAsync` paths and can reject:

- empty/whitespace keys
- keys longer than `CacheStampedeOptions.MaxKeyLength`
- keys containing control characters

### 3.2 Tags

Tag normalization (`CacheTagPolicy.NormalizeTag` / `NormalizeTags`) is:

- trim leading/trailing whitespace
- ignore null/whitespace entries
- deduplicate using ordinal comparison
- preserve case (no lowercasing)

### 3.3 Zones

Zones are represented as reserved tags with the prefix:

`zone:`

`CacheTagConventions.ToZoneTag("catalog")` -> `zone:catalog`

## 4. Reserved Key Namespaces

### 4.1 Hybrid cache tag-version keys

`HybridCacheService` stores tag versions as:

`vapecache:tag:v1:{normalizedTag}`

Payload format is 8-byte little-endian `Int64` version.

### 4.2 ASP.NET output-cache store keys

`VapeCacheOutputCacheStore` composes entry keys as:

`{KeyPrefix}:{frameworkOutputCacheKey}`

Default `KeyPrefix`: `vapecache:output`

### 4.3 ASP.NET output-cache tag-version keys

`VapeCacheOutputCacheStore` stores output tag versions as:

`{KeyPrefix}:tag-version:{normalizedTag}`

Payload format is 8-byte little-endian `Int64` version.

## 5. Tagged Payload Envelopes

### 5.1 Hybrid cache tagged envelope

Prefix:

`VCTAG1:`

Body:

JSON object with:

- `Payload` (byte[])
- `TagVersions` (dictionary tag -> version)

If an entry has no tags, payload is stored directly (no envelope).

### 5.2 ASP.NET output-cache envelope

Prefix:

`VCOUT1:`

Body:

JSON object with:

- `Payload` (byte[])
- `TagVersions` (dictionary tag -> version)

If tag indexing is disabled or tags are empty, payload is stored directly.

## 6. Collision Avoidance Guidance

To avoid collisions and operational ambiguity:

- do not write application keys under `vapecache:tag:v1:*`
- do not write application keys under `{OutputKeyPrefix}:tag-version:*`
- prefer explicit namespaces such as:
  - `app:{bounded-context}:{entity}:{id}`
  - `query:{bounded-context}:{name}:{hash}`

## 7. Determinism Guidance

For stable keys:

- include all cache-relevant input dimensions in the key
- use fixed ordering for composite parts
- avoid non-deterministic values (timestamps, GUIDs) unless intentionally unique
- prefer explicit version segments when schema/serialization shape changes

## 8. Operational Notes

- Invalidation is version-based for tags/zones and does not require full Redis key scans.
- Stale tagged entries are lazily discarded on read when stored and current tag versions differ.
