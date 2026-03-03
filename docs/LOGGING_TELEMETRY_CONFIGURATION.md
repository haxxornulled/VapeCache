# Logging and Telemetry Configuration

This guide is the source of truth for configuring:

- Serilog console logging
- optional Seq sink
- optional Serilog OTLP logs sink
- OpenTelemetry metrics/traces exporters
- grocery benchmark/demo log verbosity controls

It reflects the current runtime behavior in:

- `VapeCache.Infrastructure/DependencyInjection/VapeCacheSerilogExtensions.cs`
- `VapeCache.Console/Program.cs`
- `VapeCache.Console/appsettings.json`

## Design Goals

- Keep baseline logging available even when external sinks fail.
- Make Seq and OTLP opt-in.
- Keep configuration host-owned (appsettings/env vars).
- Prevent duplicate sink registration when users already define sinks in `Serilog:WriteTo`.

## Effective Defaults

Current default behavior from `VapeCache.Console/appsettings.json`:

- Console logging: enabled (`Serilog:WriteTo` includes `Console`)
- Seq sink: disabled (`Serilog:Seq:Enabled=false`)
- Serilog OTLP logs sink: disabled (`Serilog:OpenTelemetry:Enabled=false`)
- OpenTelemetry metrics/traces exporter: disabled (`OpenTelemetry:Otlp:Endpoint=null`)
- Fallback console guardrail: enabled (`Serilog:FallbackConsole:Enabled=true`)

## Precedence Rules

## 1) Seq sink registration

Seq is added dynamically only when all conditions are true:

1. `Serilog:Seq:Enabled=true`
2. `Serilog:WriteTo` does not already contain a `Seq` sink
3. `Serilog:Seq:ServerUrl` is a valid absolute URL

This avoids duplicate Seq sinks.

## 2) Console fallback registration

Console fallback is added dynamically only when:

1. `Serilog:FallbackConsole:Enabled=true` (default true)
2. `Serilog:WriteTo` does not already contain a `Console` sink

This guarantees at least one local sink unless explicitly disabled.

## 3) Serilog OTLP logs endpoint resolution

When `Serilog:OpenTelemetry:Enabled=true`, endpoint lookup is:

1. `Serilog:OpenTelemetry:Endpoint`
2. `OpenTelemetry:Otlp:Endpoint`
3. `OTEL_EXPORTER_OTLP_ENDPOINT`

If none is set, Serilog OTLP sink is not added.

## 4) OpenTelemetry metrics/traces endpoint resolution

For `AddOtlpExporter` in `Program.cs`, endpoint lookup is:

1. `OpenTelemetry:Otlp:Endpoint`
2. `OTEL_EXPORTER_OTLP_ENDPOINT`

If none is set, metrics/traces OTLP exporters are not registered.

## Configuration Keys

## Serilog baseline

```json
{
  "Serilog": {
    "Using": [ "Serilog.Sinks.Console", "Serilog.Enrichers.Span" ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}:{SpanId}) {Message:lj}{NewLine}{Exception}"
        }
      }
    ],
    "Enrich": [ "FromLogContext", "WithSpan" ],
    "Properties": {
      "Application": "VapeCache.Console"
    }
  }
}
```

## Serilog fallback guardrail

```json
{
  "Serilog": {
    "FallbackConsole": {
      "Enabled": true,
      "OutputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}:{SpanId}) {Message:lj}{NewLine}{Exception}"
    }
  }
}
```

## Seq sink (dynamic)

```json
{
  "Serilog": {
    "Seq": {
      "Enabled": true,
      "ServerUrl": "http://localhost:5341",
      "ApiKey": null,
      "RestrictedToMinimumLevel": "Information",
      "BatchPostingLimit": 1000,
      "PeriodMs": 2000
    }
  }
}
```

## Serilog OTLP logs sink

```json
{
  "Serilog": {
    "OpenTelemetry": {
      "Enabled": true,
      "Endpoint": "http://otel-collector:4318",
      "Protocol": "HttpProtobuf"
    }
  }
}
```

## OpenTelemetry metrics/traces exporter

```json
{
  "OpenTelemetry": {
    "Otlp": {
      "Endpoint": "http://otel-collector:4318"
    }
  }
}
```

## Environment Variable Mapping

Use `__` for nested config keys:

- `Serilog__Seq__Enabled=true`
- `Serilog__Seq__ServerUrl=http://seq-host:5341`
- `Serilog__Seq__ApiKey=...`
- `Serilog__FallbackConsole__Enabled=true`
- `Serilog__OpenTelemetry__Enabled=true`
- `Serilog__OpenTelemetry__Endpoint=http://otel-collector:4318`
- `OpenTelemetry__Otlp__Endpoint=http://otel-collector:4318`
- `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318`

Grocery/benchmark verbosity controls:

- `VAPECACHE_GROCERYSTORE_VERBOSE=true`
- `VAPECACHE_BENCH_LOG_LEVEL=Debug`

## Production Recipes

## Recipe A: Console-only safe baseline

Use when you want zero external logging dependencies.

```json
{
  "Serilog": {
    "Seq": { "Enabled": false },
    "OpenTelemetry": { "Enabled": false },
    "FallbackConsole": { "Enabled": true }
  },
  "OpenTelemetry": {
    "Otlp": { "Endpoint": null }
  }
}
```

## Recipe B: Console + Seq with fallback

Use when Seq is primary aggregation, but console must always remain available.

```json
{
  "Serilog": {
    "Seq": {
      "Enabled": true,
      "ServerUrl": "http://seq.internal:5341"
    },
    "FallbackConsole": { "Enabled": true }
  }
}
```

Notes:

- Keep `Console` in `Serilog:WriteTo` for deterministic local visibility.
- If Seq is down, console logs still flow.

## Recipe C: OTLP for metrics/traces only

Use when logs stay local/Seq, and traces/metrics go to collector.

```json
{
  "Serilog": {
    "OpenTelemetry": { "Enabled": false }
  },
  "OpenTelemetry": {
    "Otlp": { "Endpoint": "http://otel-collector:4318" }
  }
}
```

## Recipe D: Full OTLP + optional Seq

Use when you want logs, traces, and metrics exported.

```json
{
  "Serilog": {
    "Seq": {
      "Enabled": true,
      "ServerUrl": "http://seq.internal:5341"
    },
    "OpenTelemetry": {
      "Enabled": true,
      "Endpoint": "http://otel-collector:4318",
      "Protocol": "HttpProtobuf"
    }
  },
  "OpenTelemetry": {
    "Otlp": {
      "Endpoint": "http://otel-collector:4318"
    }
  }
}
```

## Failure Behavior Expectations

## Seq unavailable

- Seq sink may fail to ship events to Seq.
- Console logging remains active and should continue to show runtime logs.
- Application startup should not depend on Seq availability.

## OTLP endpoint missing

- Serilog OTLP logs sink is skipped unless endpoint is explicitly set.
- Metrics/traces exporters are not registered unless endpoint is explicitly set.

## OTLP endpoint invalid

- Invalid endpoint URI causes exporter/sink setup to be skipped.
- Console logging remains active.

## Grocery Verbose Logging

When `GroceryStoreStress:Enabled=true`:

- `VAPECACHE_GROCERYSTORE_VERBOSE=true` increases grocery/infrastructure logging detail.
- `VAPECACHE_BENCH_LOG_LEVEL` controls benchmark harness verbosity in comparison mode.

Quick run example (PowerShell):

```powershell
$env:VAPECACHE_GROCERYSTORE_VERBOSE = "true"
$env:VAPECACHE_BENCH_LOG_LEVEL = "Debug"
dotnet run --project VapeCache.Console/VapeCache.Console.csproj -c Release
```

## Validation Checklist

1. Build:

```bash
dotnet build VapeCache.Console/VapeCache.Console.csproj -c Release
```

2. Validate Seq fallback:

```powershell
$env:Serilog__Seq__Enabled = "true"
$env:Serilog__Seq__ServerUrl = "http://127.0.0.1:65534"
dotnet run --project VapeCache.Console/VapeCache.Console.csproj -c Release
```

Expected: console logs appear even though Seq is unreachable.

3. Validate OTLP activation:

```powershell
$env:OpenTelemetry__Otlp__Endpoint = "http://localhost:4318"
dotnet run --project VapeCache.Console/VapeCache.Console.csproj -c Release
```

Expected: metrics/traces exporters are registered.

## Operational Recommendations

- Keep console sink enabled in all environments as a safety net.
- Treat Seq and OTLP exporters as additive outputs, not required startup dependencies.
- Use environment variables in production for secrets (`Serilog__Seq__ApiKey`) and endpoint overrides.
- Keep `Serilog:OpenTelemetry:Enabled` and `OpenTelemetry:Otlp:Endpoint` explicit to avoid ambiguity.
