# Release Notes: Documentation Positioning Update (2026-04-12)

## External Changelog Paragraph

This update sharpens VapeCache documentation and product positioning to better reflect the native Redis-first runtime model, clarifies that production runtime packages are not coupled to StackExchange.Redis, and improves guidance for interoperability scenarios (including distributed-cache bridge and FusionCache L2 usage) without overreaching on universal performance claims. It also adds dedicated Microsoft HybridCache documentation and aligns package-level messaging for a more consistent, professional onboarding story across README surfaces.

## Internal Release Notes

### What changed

1. Repositioned top-level docs around VapeCache as a Redis-first runtime with native hybrid behavior and operational controls.
2. Reframed FusionCache references toward interoperability and migration guidance, not head-to-head superiority language.
3. Added explicit runtime dependency clarity: production runtime packages are not tied to StackExchange.Redis.
4. Added Microsoft HybridCache support documentation and usage guidance.
5. Updated package README messaging for consistency and added ignore coverage for local cache artifacts.

### Why it matters

1. Reduces claim-risk by aligning messaging with benchmark and comparison evidence policy.
2. Improves external credibility with clearer, product-led narrative and less comparative noise.
3. Helps evaluators understand architecture tradeoffs faster, especially around bridge versus native integration paths.
4. Simplifies adoption decisions for teams using IDistributedCache, FusionCache, or Microsoft HybridCache.
5. Lowers repository hygiene friction from generated local cache files.

### Action needed

1. Use the new phrasing patterns for website, package pages, and release communications.
2. Route future performance statements through benchmark policy language unless new comparative evidence is published.
3. Keep distributed-cache bridge messaging framed as a compatibility path and position native APIs as the primary experience.
4. Reuse the Microsoft HybridCache doc in onboarding and architecture conversations.
5. No runtime code migration is required from this documentation update alone.
