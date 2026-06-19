# VapeCache.Extensions.Logging

Centralized Serilog and OpenTelemetry logging wiring for VapeCache hosts.

## Install

```bash
dotnet add package VapeCache.Extensions.Logging
```

## Use This Package When

- you want one central logging setup for VapeCache hosts
- you want Serilog and OpenTelemetry wiring kept in one place
- you want optional file, Seq, console, OTLP, and JSON formatting support

## Usage

```csharp
using Serilog;
using VapeCache.Extensions.Logging;

builder.Host.UseSerilog((context, services, loggerConfig) =>
{
    loggerConfig.ConfigureVapeCacheLogging(
        context.Configuration,
        services,
        context.HostingEnvironment.EnvironmentName);
});
```

This package adds the VapeCache logging policy layer on top of Serilog configuration and supports optional file, Seq, console, OTLP, and JSON formatting behaviors.

## Docs

- Logging and telemetry configuration: https://github.com/haxxornulled/VapeCache/blob/main/docs/LOGGING_TELEMETRY_CONFIGURATION.md
- Quick start: https://github.com/haxxornulled/VapeCache/blob/main/docs/QUICKSTART.md
- API reference: https://github.com/haxxornulled/VapeCache/blob/main/docs/API_REFERENCE.md
