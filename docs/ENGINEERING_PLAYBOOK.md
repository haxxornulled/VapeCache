# Engineering Playbook (.NET 10 / VS 2026)

Repeatable workflow for analyzers, profiling, and benchmark proof artifacts.

## 1) Analyzer Gate

Run built-in analyzers with recommended .NET 10 settings:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-dotnet10-analysis.ps1 `
  -Configuration Release `
  -TreatWarningsAsErrors `
  -VerifyFormatting `
  -RunTests
```

What it does:

- `dotnet build` with `EnableNETAnalyzers=true`
- `AnalysisLevel=latest-recommended`
- optional `dotnet format analyzers --verify-no-changes`
- optional test run

## 2) Runtime Profiling

Collect counters and traces for a target project:

```powershell
powershell -ExecutionPolicy Bypass -File tools/profile-dotnet10.ps1 `
  -Project "VapeCache.Console/VapeCache.Console.csproj" `
  -Mode both `
  -DurationSeconds 45 `
  -Configuration Release
```

Artifacts:

- `counters.csv`
- `trace.nettrace`
- `profile-manifest.txt`

Open `trace.nettrace` in Visual Studio 2026 Performance Profiler or PerfView.

## 3) Head-to-Head + Packet Capture

Benchmark against StackExchange.Redis and capture packetization:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-with-capture.ps1 `
  -Job Short `
  -ConnectionString "redis://localhost:6379/0" `
  -Interface 1 `
  -RedisPort 6379
```

Artifacts include:

- BenchmarkDotNet outputs (`comparison.md`)
- `redis-capture.pcapng`
- `wireshark-io-stat.txt`
- `wireshark-conversations.txt`
- `run-manifest.txt`

If you do not want capture:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-head-to-head-with-capture.ps1 `
  -Job Short `
  -ConnectionString "redis://localhost:6379/0" `
  -SkipCapture
```

## 4) Visual Studio 2026 Workflow

1. Open the solution in VS 2026.
2. Run **Build** with analyzers enabled (`run-dotnet10-analysis.ps1` first).
3. Open **Performance Profiler**.
4. Collect:
   - CPU Usage
   - .NET Object Allocation
   - File I/O
   - EventPipe trace (for `trace.nettrace` correlation)
5. Compare against benchmark artifacts in `BenchmarkDotNet.Artifacts/`.

## 5) Recommended CI Order

1. `tools/run-dotnet10-analysis.ps1`
2. `dotnet test VapeCache.Tests/VapeCache.Tests.csproj -c Release`
3. `tools/run-head-to-head-benchmarks.ps1 -Job Short`
4. Optional nightly: `tools/run-head-to-head-with-capture.ps1`
