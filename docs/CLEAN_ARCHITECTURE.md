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
- independent of `Application` and `Infrastructure`
- may depend on `Core` for shared domain policy primitives

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
- `Abstractions -> (Application|Infrastructure)`  
`Abstractions -> Core` is allowed for shared domain policy primitives exposed through public contracts.
- `Infrastructure -> Application`

## Policy Ownership

Business/domain policies belong in `VapeCache.Core`:
- cache tag normalization and zone-tag rules
- stampede profile default values
- domain invariants and value semantics

Technical/operational policies belong in `VapeCache.Infrastructure`:
- socket and transport tuning
- mux autoscaling/coalescing/runtime normalization
- protocol and connection-level retry/error handling

## Enforcement

Architecture checks live in:
- `VapeCache.Tests/Architecture/CleanArchitectureDependencyTests.cs`

These tests verify core clean-architecture boundaries at build/test time.
