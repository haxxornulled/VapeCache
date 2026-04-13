# Wrapper Plugin Guide Status

The historical wrapper/plugin demo content depended on the removed `VapeCache.Console` host and its plugin extension sample.

That surface is not part of the current OSS repository state.

## Why This File Still Exists

This archival note prevents older links from landing on invalid instructions that refer to deleted code such as:

- `VapeCache.Console/Plugins/IVapeCachePlugin.cs`
- `VapeCache.Console/Plugins/PluginDemoHostedService.cs`
- `VapeCache.Console/Plugins/SampleCatalogPlugin.cs`

## Current Guidance

For active OSS extension points, use:

- [API_REFERENCE.md](API_REFERENCE.md)
- [ASPIRE_INTEGRATION.md](ASPIRE_INTEGRATION.md)
- [CONFIGURATION.md](CONFIGURATION.md)
- [DEMO_HOST_BLUEPRINT.md](DEMO_HOST_BLUEPRINT.md)

If plugin samples return in the future, restore code and tests first, then replace this archival note with current documentation.
