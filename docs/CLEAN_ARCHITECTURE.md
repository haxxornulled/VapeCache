# Clean Architecture Boundaries

This document defines the layer model for VapeCache and the allowed dependency flow.

## Layer Model

1. `VapeCache.Core`
Core domain primitives and cross-cutting invariants:
- domain building blocks (`AggregateRoot`, `Entity`, `ValueObject`, `DomainEvent`)
- guard/validation primitives
- zero infrastructure dependencies

2. `VapeCache.Application`
Use-case contracts and orchestration abstractions:
- command/query contracts
- handler and pipeline abstractions
- depends on `VapeCache.Core`

3. `VapeCache.Abstractions`
Public API contracts for consumers:
- cache, connection, module interfaces
- options/DTOs and public surface
- independent of `Application`, `Core`, and `Infrastructure`

4. `VapeCache.Infrastructure`
Transport and runtime implementations:
- Redis transport, mux, protocol parser, telemetry
- implements `VapeCache.Abstractions`
- must not depend on `VapeCache.Application`

5. Outer Adapters
Host and integration projects (`Console`, `Extensions.*`, `Persistence`, `Reconciliation`, benchmarks/tests) compose the runtime and wire adapters.

## Dependency Rule

Dependencies point inward toward abstractions and core policies, never from inner layers to outer implementations.

Allowed baseline flow:
- `Application -> Core`
- `Infrastructure -> Abstractions`
- outer adapters -> (`Abstractions`, `Infrastructure`, `Application`, `Core`) as needed

Disallowed:
- `Core -> *VapeCache.*`
- `Application -> Infrastructure`
- `Abstractions -> (Core|Application|Infrastructure)`
- `Infrastructure -> Application`

## Enforcement

Architecture checks live in:
- `VapeCache.Tests/Architecture/CleanArchitectureDependencyTests.cs`

These tests verify core clean-architecture boundaries at build/test time.
