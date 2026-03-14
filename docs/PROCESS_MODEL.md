# Process Model

This document defines the engineering process model used to move runtime changes from implementation to release with low surprise.

## 1. Change Lifecycle

```mermaid
flowchart TD
    A["Design / requirements"] --> B["Implementation in bounded layer"]
    B --> C["Unit + integration tests"]
    C --> D["Perf/behavior validation (HeadToHead + GroceryStore)"]
    D --> E{"Passes build/test/perf gates?"}
    E -- No --> F["Fix + re-run validations"]
    F --> C
    E -- Yes --> G["Docs + package metadata update"]
    G --> H["Version bump + package publish"]
    H --> I["Release notes + GitHub release"]
```

## 2. Runtime Validation Loop

```mermaid
sequenceDiagram
    autonumber
    participant Dev as Engineer
    participant CI as Local/CI gates
    participant GS as GroceryStore stress
    participant H2H as HeadToHead benchmark

    Dev->>CI: dotnet build/test (Release)
    CI-->>Dev: pass/fail
    Dev->>GS: run high-concurrency scenario
    GS-->>Dev: throughput, hits/misses, breaker/fallback stats, memory trend
    Dev->>H2H: run apples + optimized tracks
    H2H-->>Dev: latency/throughput/allocation diffs
    Dev->>CI: finalize with docs + release metadata
```

## 3. No-Surprises Release Gate

```mermaid
stateDiagram-v2
    [*] --> BuildGreen
    BuildGreen --> TestsGreen
    TestsGreen --> StressValidated
    StressValidated --> DocsSynced
    DocsSynced --> Versioned
    Versioned --> Published
    Published --> [*]

    BuildGreen --> BuildGreen: Fix compiler/analyzer regressions
    TestsGreen --> TestsGreen: Fix behavioral regressions
    StressValidated --> StressValidated: Fix concurrency/memory/perf anomalies
```

## 4. Required Evidence Before Publish

- Release build succeeds with zero errors.
- Core test suites pass.
- GroceryStore run shows stable breaker/fallback behavior and sane hit/miss accounting.
- HeadToHead run completes both tracks and reports workload integrity metrics.
- Memory trend sampling does not show unbounded process growth during stress.
- Docs and package matrices match current published package set.
