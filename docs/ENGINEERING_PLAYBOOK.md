# Engineering Playbook

Current repeatable workflow for this repository:

## 1. Analyzer And Build Gate

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-dotnet10-analysis.ps1 `
  -Configuration Release `
  -TreatWarningsAsErrors `
  -VerifyFormatting `
  -RunTests
```

## 2. Profiling

Use `tools/profile-dotnet10.ps1` against an active project in the current repo surface, for example:

```powershell
powershell -ExecutionPolicy Bypass -File tools/profile-dotnet10.ps1 `
  -Project "VapeCache.UI/VapeCache.UI.csproj" `
  -Mode both `
  -DurationSeconds 45 `
  -Configuration Release
```

## 3. Benchmarks

Use:

- [BENCHMARKING.md](BENCHMARKING.md)
- [VapeCache.Benchmarks/README.md](../VapeCache.Benchmarks/README.md)

Do not use deleted commands that targeted `VapeCache.Console` or `VapeCache.Benchmarks.Runner.csproj`.
