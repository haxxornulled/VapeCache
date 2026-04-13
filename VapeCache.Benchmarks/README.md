# VapeCache Benchmarks

Fresh BenchmarkDotNet harness for focused microbenchmarks against the current runtime code.

Memory allocation data is collected centrally through the shared BenchmarkDotNet config. Live backend reports and comparison exports include the `Allocated` column from `MemoryDiagnoser`, so allocation regressions show up alongside latency.

## Run Default Benchmarks

```powershell
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj
```

The default command runs only the `Micro` benchmark category so a clean clone does not require live Redis or KeyDB containers.

## Filter To One Benchmark Class

```powershell
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj -- --filter "*RedisRespProtocolBenchmarks*"
```

## Current Scope

- `RedisRespProtocolBenchmarks`: RESP command sizing and write hot paths.
- `RespParserLiteBenchmarks`: allocation-free RESP parser hot paths.
- `RedisBackendRoundTripBenchmarks`: live `Ping`, `Set`, and `Get` round trips against local Redis and KeyDB containers.

Start here, measure one hotspot at a time, and keep external Redis dependencies out of the default benchmark pass.

## Live Backends

Bring up local containers:

```powershell
docker compose -f .\infra\docker-compose.benchmarks.yml up -d
```

Run the live backend benchmark against both engines:

```powershell
pwsh .\tools\run-live-benchmarks.ps1 -EnsureContainers
```

The default live run is a smoke pass: Redis and KeyDB, `Ping`/`Set`/`Get`, 256-byte payloads, short BenchmarkDotNet job, and an exported Redis-vs-KeyDB comparison markdown report.

Run the explicit full live pass:

```powershell
pwsh .\tools\run-live-benchmarks.ps1 -Mode full -EnsureContainers
```

Run against only one backend:

```powershell
pwsh .\tools\run-live-benchmarks.ps1 -Backend redis
pwsh .\tools\run-live-benchmarks.ps1 -Backend keydb
```

You can also opt in manually from the command line:

```powershell
dotnet run -c Release --project .\VapeCache.Benchmarks\VapeCache.Benchmarks.csproj -- --include-live --filter "*RedisBackendRoundTripBenchmarks*"
```

Comparison reports are written to `BenchmarkDotNet.Artifacts\results\RedisVsKeyDbComparison.md` when both backends are included in the live run.
