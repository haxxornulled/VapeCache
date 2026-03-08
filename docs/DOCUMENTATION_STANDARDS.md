# Documentation Standards

This repository is maintained as long-term production code. Documentation is treated as part of the API contract.

## Rules

1. Public APIs must have XML documentation (`///`):
   - Public classes/records/interfaces/enums
   - Public methods/properties/fields/events
2. Avoid commented-out code. Remove dead code instead of leaving disabled blocks.
3. Every options setting must be documented and discoverable in `docs/SETTINGS_REFERENCE.md`.
4. Quick-start docs must match the current runtime defaults (especially telemetry/instrumentation defaults).

## Enforcement Workflow

Run before pushing:

```powershell
dotnet build VapeCache.slnx -c Release
powershell -ExecutionPolicy Bypass -File .\tools\Generate-SettingsReference.ps1
```

Optional strict XML-doc check (can be run per project during cleanup waves):

```powershell
dotnet build .\VapeCache.Core\VapeCache.Core.csproj -c Release -p:GenerateDocumentationFile=true -warnaserror:CS1591
```

## Cleanup Target

Long-term target is complete XML-doc coverage for all shipped surface area and generated settings docs on every options change.
