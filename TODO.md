# TODO

- Migrate the repo to real NuGet Central Package Management (`Directory.Packages.props`) instead of property-based version indirection.
- Use a staged rollout: start with Aspire, OpenTelemetry, Serilog, and shared `Microsoft.Extensions.*` packages, then widen across the solution.
- Consider using `CpmMigrator` to reduce manual churn and mismatch risk during the conversion.
